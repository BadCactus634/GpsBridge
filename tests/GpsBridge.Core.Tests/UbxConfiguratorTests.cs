using System.Text;
using GpsBridge.Core.Nmea;
using GpsBridge.Core.Ubx;
using Xunit;

namespace GpsBridge.Core.Tests;

/// <summary>Ricevitore finto: tiene la configurazione per layer e risponde ACK/NAK/VALGET/MON-VER.</summary>
sealed class FakeReceiver
{
    public readonly FrameSplitter Splitter = new();
    public readonly Dictionary<int, Dictionary<uint, ulong>> Layers = new() { [0] = new(), [1] = new(), [2] = new() }; // RAM,BBR,Flash
    public int ValSets, Writes;
    public bool ResetSent;
    public bool Silent;
    public Func<IReadOnlyDictionary<uint, ulong>, bool> Accepts = _ => true;

    public void Send(byte[] frame)
    {
        if (Silent) return;
        Assert.True(UbxFrame.TryRead(frame, out var cls, out var id, out var p));
        if (cls == 0x0A && id == 0x04)
        {
            var pl = new byte[30 + 10 + 30];
            Encoding.ASCII.GetBytes("EXT CORE 5.10 (fake)").CopyTo(pl, 0);
            Encoding.ASCII.GetBytes("000A0000").CopyTo(pl, 30);
            Encoding.ASCII.GetBytes("PROTVER=34.00").CopyTo(pl, 40);
            Splitter.Feed(UbxFrame.Build(0x0A, 0x04, pl));
        }
        else if (cls == 0x06 && id == 0x8A)
        {
            ValSets++;
            var items = new Dictionary<uint, ulong>();
            for (int i = 4; i < p.Length;)
            {
                uint k = BitConverter.ToUInt32(p, i); i += 4;
                int n = UbxKeys.SizeOf(k); ulong v = 0;
                for (int b = 0; b < n; b++) v |= (ulong)p[i + b] << (8 * b);
                i += n; items[k] = v;
            }
            if (!Accepts(items)) { Splitter.Feed(UbxFrame.Build(0x05, 0x00, new byte[] { 0x06, 0x8A })); return; }
            if ((p[1] & 1) != 0) foreach (var kv in items) Layers[0][kv.Key] = kv.Value;
            if ((p[1] & 2) != 0) foreach (var kv in items) Layers[1][kv.Key] = kv.Value;
            if ((p[1] & 4) != 0) foreach (var kv in items) Layers[2][kv.Key] = kv.Value;
            Writes++;
            Splitter.Feed(UbxFrame.Build(0x05, 0x01, new byte[] { 0x06, 0x8A }));
        }
        else if (cls == 0x06 && id == 0x8C)
        {
            for (int i = 4; i + 4 <= p.Length; i += 4)
            {
                uint k = BitConverter.ToUInt32(p, i);
                if ((p[1] & 2) != 0) Layers[1].Remove(k);
                if ((p[1] & 4) != 0) Layers[2].Remove(k);
            }
        }
        else if (cls == 0x06 && id == 0x04) ResetSent = true;
        else if (cls == 0x06 && id == 0x8B)
        {
            int layer = p[1];
            var resp = new List<byte> { 1, p[1], 0, 0 };
            for (int i = 4; i + 4 <= p.Length; i += 4)
            {
                uint k = BitConverter.ToUInt32(p, i);
                if (!Layers[layer].TryGetValue(k, out var v)) continue;
                resp.AddRange(BitConverter.GetBytes(k));
                for (int b = 0; b < UbxKeys.SizeOf(k); b++) resp.Add((byte)(v >> (8 * b)));
            }
            Splitter.Feed(UbxFrame.Build(0x06, 0x8B, resp.ToArray()));
        }
    }
}

public class UbxConfiguratorTests
{
    const byte All = UbxFrame.LayerRam | UbxFrame.LayerBbr | UbxFrame.LayerFlash;

    static (FakeReceiver rx, UbxConfigurator cfg) Make(int timeoutMs = 500)
    {
        var rx = new FakeReceiver();
        return (rx, new UbxConfigurator(rx.Send, rx.Splitter, TimeSpan.FromMilliseconds(timeoutMs)));
    }

    [Fact]
    public async Task ReadVersion_returns_sw_hw_and_extensions()
    {
        var (_, cfg) = Make();
        var v = await cfg.ReadVersionAsync();
        Assert.Contains("EXT CORE 5.10", v);
        Assert.Contains("000A0000", v);
        Assert.Contains("PROTVER=34.00", v);
    }

    [Fact]
    public async Task Fresh_receiver_gets_one_valset_to_all_layers()
    {
        var (rx, cfg) = Make();
        var r = await cfg.ApplyAsync(ReceiverProfile.Best, All);
        Assert.True(r.Ok);
        Assert.False(r.AlreadyOk);
        Assert.Equal(1, rx.ValSets);
        Assert.Equal(6, r.Active.Count);
        Assert.Equal(1UL, rx.Layers[2][UbxKeys.GloEna]);
    }

    [Fact]
    public async Task Already_configured_receiver_gets_no_write()
    {
        var (rx, cfg) = Make();
        foreach (var (k, v) in ReceiverProfile.Best.Flatten(6)) { rx.Layers[0][k] = v; rx.Layers[2][k] = v; }
        var r = await cfg.ApplyAsync(ReceiverProfile.Best, All);
        Assert.True(r.Ok); Assert.True(r.AlreadyOk);
        Assert.Equal(0, rx.ValSets);
    }

    [Fact]
    public async Task Nak_drops_constellations_from_the_tail_and_reports_them()
    {
        var (rx, cfg) = Make();
        rx.Accepts = items => !(items.TryGetValue(UbxKeys.GloEna, out var g) && g == 1);
        var r = await cfg.ApplyAsync(ReceiverProfile.Best, All);
        Assert.True(r.Ok);
        Assert.Equal(new[] { "GPS", "Galileo", "BeiDou" }, r.Active);
        Assert.Equal(new[] { "GLONASS", "QZSS", "SBAS" }, r.Dropped);
    }

    [Fact]
    public async Task Second_run_after_fallback_does_not_rewrite_flash()
    {
        var (rx, cfg) = Make();
        rx.Accepts = items => !(items.TryGetValue(UbxKeys.GloEna, out var g) && g == 1);
        await cfg.ApplyAsync(ReceiverProfile.Best, All);
        int before = rx.Writes;
        var r = await cfg.ApplyAsync(ReceiverProfile.Best, All);
        Assert.True(r.Ok); Assert.True(r.AlreadyOk);
        Assert.Equal(before, rx.Writes);   // i tentativi rifiutati (NAK) non scrivono, quello gia' a posto non riscrive la flash
        Assert.Equal(new[] { "GPS", "Galileo", "BeiDou" }, r.Active);
    }

    [Fact]
    public async Task Silent_receiver_gives_clear_error_not_a_hang()
    {
        var (rx, cfg) = Make(100);
        rx.Silent = true;
        var r = await cfg.ApplyAsync(ReceiverProfile.Best, All);
        Assert.False(r.Ok);
        Assert.Contains("risposta", r.Error);
    }
}

