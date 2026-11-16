using GpsBridge.Core.Settings;
using GpsBridge.Core.Ubx;
using Xunit;

namespace GpsBridge.Core.Tests;

public class AppSettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "gpsb-set-" + Guid.NewGuid().ToString("N"));
    public AppSettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var path = Path.Combine(_dir, "s.json");
        var s = new AppSettings { KmlPort = 9000, Units = Geo.UnitSystem.Nautical, CoordFormat = Geo.CoordFormat.Utm, AutoStartRecording = true };
        s.Receiver.RateHz = 10; s.Receiver.Glonass = false;
        s.Save(path);
        var l = AppSettings.Load(path);
        Assert.Equal(9000, l.KmlPort);
        Assert.Equal(Geo.UnitSystem.Nautical, l.Units);
        Assert.Equal(Geo.CoordFormat.Utm, l.CoordFormat);
        Assert.True(l.AutoStartRecording);
        Assert.Equal(10, l.Receiver.RateHz);
        Assert.False(l.Receiver.Glonass);
    }

    [Fact]
    public void Missing_or_corrupt_file_gives_defaults()
    {
        Assert.Equal(8765, AppSettings.Load(Path.Combine(_dir, "nope.json")).KmlPort);
        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "{ questo non e' json");
        var d = AppSettings.Load(bad);
        Assert.True(d.KmlEnabled);
        Assert.Equal(5, d.Receiver.RateHz);
    }

    [Fact]
    public void Receiver_options_build_the_profile()
    {
        var o = new ReceiverOptions { Glonass = false, RateHz = 10, UartBaud = 115200 };
        var p = ReceiverProfile.FromOptions(o);
        Assert.DoesNotContain(p.Constellations, c => c.Name == "GLONASS");
        var items = p.Flatten(p.Constellations.Count).ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal(0UL, items[UbxKeys.GloEna]);          // spenta esplicitamente
        Assert.Equal(100UL, items[UbxKeys.RateMeas]);
        Assert.Equal(115200UL, items[UbxKeys.Uart1Baud]);
    }

    [Fact]
    public void Best_equals_default_options_profile()
    {
        var a = ReceiverProfile.Best.Flatten(6).OrderBy(x => x.Key).ToList();
        var b = ReceiverProfile.FromOptions(new ReceiverOptions()).Flatten(6).OrderBy(x => x.Key).ToList();
        Assert.Equal(a, b);
    }
}
