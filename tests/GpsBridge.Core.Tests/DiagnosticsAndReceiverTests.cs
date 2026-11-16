using GpsBridge.Core.Nmea;
using GpsBridge.Core.Settings;
using GpsBridge.Core.Ubx;
using Xunit;

namespace GpsBridge.Core.Tests;

public class DiagnosticsAndReceiverTests
{
    static GnssState Epoch1()
    {
        var st = new GnssState();
        foreach (var l in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "data", "sample-multi-gnss.nmea")).Take(11))
            if (NmeaSentence.TryParse(l, out var s)) st.Apply(s);
        return st;
    }

    [Fact]
    public void Rows_are_ordered_and_counted()
    {
        var rows = DiagnosticsModel.Rows(Epoch1());
        Assert.Equal(new[] { "GPS", "Galileo", "BeiDou", "GLONASS" }, rows.Select(r => r.Name));
        var gps = rows[0];
        Assert.Equal(4, gps.Used); Assert.Equal(5, gps.InView);
        Assert.Equal((46 + 41 + 39 + 30 + 25) / 5.0, gps.AvgSnr!.Value, 3);
    }

    [Fact]
    public void Sky_maps_azimuth_and_elevation()
    {
        var sky = DiagnosticsModel.Sky(Epoch1());
        var zen = sky.Single(p => p.Constellation == "GPS" && p.Prn == 4);   // elev 50, azim 100
        double r = 40 / 90.0;
        Assert.Equal(r * Math.Sin(100 * Math.PI / 180), zen.X, 6);
        Assert.Equal(r * Math.Cos(100 * Math.PI / 180), zen.Y, 6);
        Assert.Equal("Nessun fix", DiagnosticsModel.FixText(0));
    }

    [Fact]
    public async Task Controller_apply_best_and_read_diff()
    {
        var rx = new FakeReceiver();
        var cfg = new UbxConfigurator(rx.Send, rx.Splitter, TimeSpan.FromMilliseconds(500));
        var ctl = new ReceiverController(cfg, rx.Send);
        var r = await ctl.ApplyBestAsync();
        Assert.True(r.Ok);
        var st = await ctl.ReadAsync(new ReceiverOptions());
        Assert.True(st.DiffFromOptions!.IsEmpty);
        Assert.Contains("EXT CORE", st.Version);
        var changed = new ReceiverOptions { RateHz = 10 };
        Assert.False((await ctl.ReadAsync(changed)).DiffFromOptions!.IsEmpty);
    }

    [Fact]
    public async Task Reset_to_defaults_removes_keys_from_flash_and_sends_reset()
    {
        var rx = new FakeReceiver();
        var cfg = new UbxConfigurator(rx.Send, rx.Splitter, TimeSpan.FromMilliseconds(500));
        var ctl = new ReceiverController(cfg, rx.Send);
        await ctl.ApplyBestAsync();
        Assert.True(rx.Layers[2].ContainsKey(UbxKeys.RateMeas));
        ctl.ResetToDefaults();
        Assert.False(rx.Layers[2].ContainsKey(UbxKeys.RateMeas));
        Assert.True(rx.ResetSent);
    }
}
