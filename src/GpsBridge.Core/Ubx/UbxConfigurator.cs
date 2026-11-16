using System.Text;
using GpsBridge.Core.Nmea;

namespace GpsBridge.Core.Ubx;

public sealed record ConfigDiff(IReadOnlyList<(uint Key, ulong Wanted, ulong? Actual)> Items)
{
    public bool IsEmpty => Items.Count == 0;
}

/// <summary>Capacita' del ricevitore da UBX-MON-GNSS. Bit: 0 GPS, 1 GLONASS, 2 BeiDou, 3 Galileo.</summary>
public sealed record GnssCaps(byte Supported, byte Enabled, int Simultaneous);

/// <param name="Dropped">Costellazioni non attivate: rifiutate (NAK) o oltre il massimo che il chip usa insieme.</param>
public sealed record ApplyResult(bool Ok, bool AlreadyOk, IReadOnlyList<string> Active, IReadOnlyList<string> Dropped, string? Error, GnssCaps? Caps = null);

/// <summary>Legge/scrive la configurazione del ricevitore via UBX (VALGET/VALSET) sul flusso gia' in uso.</summary>
public sealed class UbxConfigurator
{
    readonly Action<byte[]> _send;
    readonly FrameSplitter _splitter;
    readonly TimeSpan _timeout;

    public UbxConfigurator(Action<byte[]> send, FrameSplitter splitter, TimeSpan? timeout = null)
    {
        _send = send; _splitter = splitter; _timeout = timeout ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Invia un frame e attende la prima risposta accettata da <paramref name="match"/>; null se scade.</summary>
    async Task<(byte Cls, byte Id, byte[] Payload)?> RequestAsync(byte[] frame, Func<byte, byte, byte[], bool> match)
    {
        var tcs = new TaskCompletionSource<(byte, byte, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(byte[] f)
        {
            if (f.Length >= 8 && match(f[2], f[3], f[6..^2])) tcs.TrySetResult((f[2], f[3], f[6..^2]));
        }
        _splitter.Ubx += Handler;
        try
        {
            _send(frame);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(_timeout));
            if (done != tcs.Task) return null;
            return await tcs.Task;
        }
        finally { _splitter.Ubx -= Handler; }
    }

    static bool IsAckFor(byte cls, byte id, byte[] p, byte reqCls, byte reqId) =>
        cls == 0x05 && (id == 0x00 || id == 0x01) && p.Length >= 2 && p[0] == reqCls && p[1] == reqId;

    static string CStr(byte[] p, int off, int len)
    {
        if (off >= p.Length) return "";
        len = Math.Min(len, p.Length - off);
        var s = Encoding.ASCII.GetString(p, off, len);
        int z = s.IndexOf('\0');
        return (z >= 0 ? s[..z] : s).Trim();
    }

    public async Task<string> ReadVersionAsync()
    {
        var r = await RequestAsync(UbxFrame.MonVer(), (c, i, _) => c == 0x0A && i == 0x04);
        if (r is null) return "(nessuna risposta dal ricevitore)";
        var p = r.Value.Payload;
        var parts = new List<string> { CStr(p, 0, 30), CStr(p, 30, 10) };
        for (int o = 40; o < p.Length; o += 30) { var e = CStr(p, o, 30); if (e.Length > 0) parts.Add(e); }
        return string.Join(" | ", parts.Where(x => x.Length > 0));
    }

    /// <summary>Differenze fra profilo (prime <paramref name="enabledGroups"/> costellazioni accese) e layer letto.</summary>
    public async Task<ConfigDiff?> DiffAsync(ReceiverProfile profile, int enabledGroups, byte getLayer)
    {
        var wanted = profile.Flatten(enabledGroups).ToList();
        var r = await RequestAsync(UbxFrame.ValGet(getLayer, wanted.Select(w => w.Key)),
            (c, i, p) => (c == 0x06 && i == 0x8B) || IsAckFor(c, i, p, 0x06, 0x8B));
        if (r is null) return null;
        var actual = r.Value.Cls == 0x06 ? UbxFrame.ParseValGet(r.Value.Payload) : new Dictionary<uint, ulong>();
        var diff = wanted.Where(w => !actual.TryGetValue(w.Key, out var v) || v != w.Value)
                         .Select(w => (w.Key, w.Value, actual.TryGetValue(w.Key, out var a) ? (ulong?)a : null)).ToList();
        return new ConfigDiff(diff);
    }

    /// <summary>Legge quante costellazioni il chip puo' usare insieme (null se il modello non risponde a MON-GNSS).</summary>
    public async Task<GnssCaps?> ReadCapsAsync()
    {
        var r = await RequestAsync(UbxFrame.MonGnss(), (c, i, _) => c == 0x0A && i == 0x28);
        if (r is null || r.Value.Payload.Length < 5) return null;
        var p = r.Value.Payload;
        return new GnssCaps(p[1], p[3], p[4]);
    }

    public async Task<ApplyResult> ApplyAsync(ReceiverProfile requested, byte layers)
    {
        // Il chip ha un tetto di costellazioni principali in uso insieme (3 sul GM10): oltre, tornerebbe al solo GPS.
        var caps = await ReadCapsAsync();
        var (profile, limited) = caps is { Simultaneous: > 0 } c0 ? requested.WithMaxMain(c0.Simultaneous) : (requested, (IReadOnlyList<string>)Array.Empty<string>());
        var names = profile.Constellations.Select(c => c.Name).ToList();
        byte getLayer = (layers & UbxFrame.LayerFlash) != 0 ? UbxFrame.GetFlash : UbxFrame.GetRam;
        const string NoReply = "nessuna risposta dal ricevitore (cavo, baud o porta sbagliati?)";

        for (int n = names.Count; n >= 1; n--)
        {
            var diff = await DiffAsync(profile, n, getLayer);
            if (diff is null) return new ApplyResult(false, false, names, Array.Empty<string>(), NoReply, caps);
            var active = names.Take(n).ToList();
            var dropped = limited.Concat(names.Skip(n)).ToList();
            if (diff.IsEmpty) return new ApplyResult(true, true, active, dropped, null, caps);

            var ack = await RequestAsync(UbxFrame.ValSet(layers, profile.Flatten(n)),
                (c, i, p) => IsAckFor(c, i, p, 0x06, 0x8A));
            if (ack is null) return new ApplyResult(false, false, names, Array.Empty<string>(), NoReply, caps);
            if (ack.Value.Id == 0x01) return new ApplyResult(true, false, active, dropped, null, caps);
            // NAK: nulla e' stato scritto, si prova con una costellazione in meno
        }
        return new ApplyResult(false, false, Array.Empty<string>(), limited.Concat(names).ToList(), "il ricevitore ha rifiutato tutte le combinazioni (NAK)", caps);
    }
}
