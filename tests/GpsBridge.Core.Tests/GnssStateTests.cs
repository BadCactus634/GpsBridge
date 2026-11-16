using GpsBridge.Core.Nmea;
using Xunit;

namespace GpsBridge.Core.Tests;

public class GnssStateTests
{
    static IEnumerable<NmeaSentence> Sample()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "sample-multi-gnss.nmea");
        foreach (var l in File.ReadAllLines(path))
            if (NmeaSentence.TryParse(l, out var s)) yield return s;
    }

    static GnssState AfterFirstEpoch()
    {
        var st = new GnssState();
        foreach (var s in Sample().Take(11)) st.Apply(s);
        return st;
    }

    [Fact]
    public void Position_is_decimal_degrees_with_sign_and_utc()
    {
        var st = AfterFirstEpoch();
        Assert.NotNull(st.Position);
        Assert.Equal(48.1173, st.Position!.Lat, 4);
        Assert.Equal(11.516667, st.Position.Lon, 5);
        Assert.Equal(545.4, st.Position.Alt!.Value, 1);
        Assert.Equal(new DateTimeOffset(2026, 1, 16, 12, 35, 19, TimeSpan.Zero), st.Position.Utc);
        Assert.Equal(22.4, st.Position.SpeedKnots!.Value, 1);
    }

    [Fact]
    public void South_and_west_are_negative()
    {
        var st = new GnssState();
        foreach (var b in new[] { "GPGGA,010203,3352.0000,S,15112.0000,W,1,05,1.0,10.0,M,0,M,," })
            Assert.True(NmeaSentence.TryParse(NmeaSentenceTests.Make(b), out var s) && Apply(st, s));
        Assert.True(st.Position!.Lat < 0);
        Assert.True(st.Position.Lon < 0);
    }
    static bool Apply(GnssState st, NmeaSentence s) { st.Apply(s); return true; }

    [Fact]
    public void Dop_and_quality()
    {
        var st = AfterFirstEpoch();
        Assert.Equal(1, st.FixQuality);
        Assert.Equal(0.9, st.Hdop!.Value, 2);
        Assert.Equal(1.8, st.Pdop!.Value, 2);
        Assert.Equal(1.5, st.Vdop!.Value, 2);
    }

    [Fact]
    public void Satellites_grouped_by_constellation_with_multi_message_gsv()
    {
        var st = AfterFirstEpoch();
        Assert.Equal(11, st.Satellites.Count);
        Assert.Equal((4, 5), st.PerConstellation["GPS"]);
        Assert.Equal((2, 2), st.PerConstellation["GLONASS"]);
        Assert.Equal((2, 2), st.PerConstellation["Galileo"]);
        Assert.Equal((2, 2), st.PerConstellation["BeiDou"]);
        var sat = st.Satellites.Single(x => x.Constellation == "GPS" && x.Prn == 4);
        Assert.Equal(50, sat.Elev);
        Assert.Equal(100, sat.Azim);
        Assert.Equal(30, sat.Snr);
        Assert.True(sat.Used);
        Assert.False(st.Satellites.Single(x => x.Constellation == "GPS" && x.Prn == 5).Used);
    }

    [Fact]
    public void No_fix_gives_null_position()
    {
        var st = new GnssState();
        foreach (var s in Sample()) st.Apply(s);
        Assert.Null(st.Position);
        Assert.Equal(0, st.FixQuality);
    }

    [Fact]
    public void Changed_event_fires()
    {
        var st = new GnssState(); int n = 0; st.Changed += () => n++;
        foreach (var s in Sample().Take(2)) st.Apply(s);
        Assert.Equal(2, n);
    }
}
