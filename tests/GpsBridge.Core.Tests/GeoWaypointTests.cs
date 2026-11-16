using GpsBridge.Core.Geo;
using GpsBridge.Core.Gpx;
using GpsBridge.Core.Kml;
using GpsBridge.Core.Waypoints;
using Xunit;

namespace GpsBridge.Core.Tests;

public class GeoMathTests
{
    [Fact]
    public void Distance_milano_roma_about_477km()
    {
        double d = GeoMath.DistanceMeters(45.4642, 9.1900, 41.9028, 12.4964);
        Assert.InRange(d / 1000, 475, 480);
    }

    [Fact]
    public void Bearing_and_cardinal()
    {
        Assert.Equal(0, GeoMath.BearingDeg(45, 9, 46, 9), 3);
        Assert.Equal(90, GeoMath.BearingDeg(0, 9, 0, 10), 3);
        Assert.Equal("N", GeoMath.Cardinal(359));
        Assert.Equal("SO", GeoMath.Cardinal(225));
    }

    [Fact]
    public void Utm_on_central_meridian_zone_32()
    {
        var s = GeoMath.Utm(45, 9).Split(' ');
        Assert.Equal("32T", s[0]);
        Assert.Equal(500000, double.Parse(s[1]), 0);
        Assert.InRange(double.Parse(s[2]), 4982949, 4982952);
    }

    [Theory]
    [InlineData("45.5565, 10.2350", 45.5565, 10.2350)]
    [InlineData("45.5565 N 10.2350 E", 45.5565, 10.2350)]
    [InlineData("10.2350 E, 45.5565 N", 45.5565, 10.2350)]
    [InlineData("45°33'23.4\"N 10°14'06\"E", 45.5565, 10.235)]
    [InlineData("45°33.39'N 10°14.1'E", 45.5565, 10.235)]
    [InlineData("-33.8688, 151.2093", -33.8688, 151.2093)]
    [InlineData("33.8688 S 151.2093 E", -33.8688, 151.2093)]
    public void Parse_accepts_common_formats(string text, double lat, double lon)
    {
        Assert.True(GeoMath.TryParseCoord(text, out var a, out var b));
        Assert.Equal(lat, a, 4); Assert.Equal(lon, b, 4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ciao")]
    [InlineData("91, 10")]
    [InlineData("45, 200")]
    [InlineData("45")]
    public void Parse_rejects_garbage(string text) => Assert.False(GeoMath.TryParseCoord(text, out _, out _));

    [Fact]
    public void Format_round_trips_through_parse()
    {
        foreach (var f in new[] { CoordFormat.Decimal, CoordFormat.Dms, CoordFormat.Ddm })
        {
            Assert.True(GeoMath.TryParseCoord(GeoMath.FormatCoord(-33.8688, 151.2093, f), out var la, out var lo), f.ToString());
            Assert.Equal(-33.8688, la, 3); Assert.Equal(151.2093, lo, 3);
        }
    }

    [Fact]
    public void Units()
    {
        Assert.Equal("850 m", GeoMath.Distance(850, UnitSystem.Metric));
        Assert.Equal("1.50 km", GeoMath.Distance(1500, UnitSystem.Metric));
        Assert.Equal("0.93 mi", GeoMath.Distance(1500, UnitSystem.Imperial));
        Assert.Equal("1.00 NM", GeoMath.Distance(1852, UnitSystem.Nautical));
        Assert.Equal("18.5 km/h", GeoMath.Speed(10, UnitSystem.Metric));
        Assert.Equal("—", GeoMath.Speed(null, UnitSystem.Metric));
    }

    [Fact]
    public void Navigation_to_target()
    {
        var n = Navigation.To(45, 9, courseDeg: 0, speedKnots: 10, 45.1, 9);
        Assert.Equal("N", n.Cardinal);
        Assert.InRange(n.DistanceM, 11000, 11200);
        Assert.Equal(0, n.RelativeDeg!.Value, 1);
        Assert.NotNull(n.Eta);
        Assert.Null(Navigation.To(45, 9, null, 0, 45.1, 9).Eta);
        Assert.Equal(-90, Navigation.To(45, 9, 90, 5, 45.1, 9).RelativeDeg!.Value, 0);
    }
}

public class WaypointTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "gpsb-wp-" + Guid.NewGuid().ToString("N"));
    public WaypointTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Add_update_remove_persist_and_fire_Changed()
    {
        var p = Path.Combine(_dir, "w.json"); int ch = 0;
        var s = new WaypointStore(p); s.Changed += () => ch++;
        var w = s.Add(Waypoint.Create("Rifugio", 46.1, 10.2, 1800, "#43A047", "chiave sotto il vaso"));
        Assert.True(s.Update(w with { Name = "Rifugio Nord", Color = "#1E4FD8" }));
        var s2 = new WaypointStore(p);
        Assert.Equal("Rifugio Nord", s2.Items.Single().Name);
        Assert.Equal("#1E4FD8", s2.Items.Single().Color);
        Assert.True(s2.Remove(w.Id));
        Assert.Empty(new WaypointStore(p).Items);
        Assert.Equal(2, ch);
    }

    [Fact]
    public void Corrupt_file_starts_empty()
    {
        var p = Path.Combine(_dir, "bad.json"); File.WriteAllText(p, "{{{");
        Assert.Empty(new WaypointStore(p).Items);
    }

    [Fact]
    public void Gpx_export_import_keeps_name_color_notes_and_skips_duplicates()
    {
        var s = new WaypointStore(null);
        s.Add(Waypoint.Create("Parcheggio", 45.1, 9.1, 100, "#FB8C00", "gratis"));
        s.Add(Waypoint.Create("Vetta & croce", 45.2, 9.2, null, "#8E24AA"));
        var f = Path.Combine(_dir, "w.gpx"); s.ExportGpx(f);
        var t = new WaypointStore(null);
        Assert.Equal(2, t.ImportGpx(f));
        Assert.Equal(0, t.ImportGpx(f));
        var w = t.Items.Single(x => x.Name == "Parcheggio");
        Assert.Equal("#FB8C00", w.Color); Assert.Equal("gratis", w.Notes); Assert.Equal(100, w.Ele!.Value, 1);
        Assert.Contains(t.Items, x => x.Name == "Vetta & croce");
    }

    [Fact]
    public void Kml_color_is_aabbggrr_and_kml_contains_waypoints()
    {
        Assert.Equal("ffe5ae1e".Length, WaypointColors.ToKml("#1EAEE5").Length);
        Assert.Equal("ffe5af1e", WaypointColors.ToKml("#1EAFE5"));
        var w = Waypoint.Create("A<b>", 45, 9, null, "#E53935");
        var kml = KmlBuilder.Position(null, Array.Empty<(double, double)>(), "x", false, new[] { w });
        var d = System.Xml.Linq.XDocument.Parse(kml);
        Assert.Contains(d.Descendants(), e => e.Name.LocalName == "name" && e.Value == "A<b>");
        Assert.Contains("ff3539e5", kml);
    }
}

public class GpxReaderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "gpsb-rd-" + Guid.NewGuid().ToString("N"));
    public GpxReaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Stats_on_a_simple_walk()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var pts = new List<TrackPoint>();
        for (int i = 0; i <= 10; i++)   // ~111 m ogni 10 s verso nord = 40 km/h, in salita di 5 m a punto
            pts.Add(new TrackPoint(45 + i * 0.001, 9, 100 + i * 5, t0.AddSeconds(i * 10)));
        var st = GpxReader.Stats(pts);
        Assert.InRange(st.DistanceM, 1105, 1120);
        Assert.Equal(TimeSpan.FromSeconds(100), st.Duration);
        Assert.Equal(TimeSpan.FromSeconds(100), st.MovingTime);
        Assert.InRange(st.AvgSpeedKnots!.Value * 1.852, 39, 41);
        Assert.InRange(st.AscentM!.Value, 45, 50);
        Assert.Equal(0, st.DescentM!.Value, 1);
        Assert.Equal(100, st.MinEle!.Value, 1); Assert.Equal(150, st.MaxEle!.Value, 1);
    }

    [Fact]
    public void Stationary_time_is_not_moving_time_and_empty_track_is_safe()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var st = GpxReader.Stats(new[] { new TrackPoint(45, 9, null, t0), new TrackPoint(45, 9, null, t0.AddSeconds(30)) });
        Assert.Equal(TimeSpan.Zero, st.MovingTime);
        Assert.Null(st.AvgSpeedKnots);
        Assert.Equal(0, GpxReader.Stats(Array.Empty<TrackPoint>()).Points);
    }

    [Fact]
    public void Recorder_file_round_trips_through_reader_and_truncated_file_is_read()
    {
        var rec = new GpxRecorder(_dir, minMoveMeters: 1);
        rec.Start();
        rec.OnFix(new Nmea.GnssFix(45, 9, 100, DateTimeOffset.UtcNow, 5, 0), 8, 1.0);
        rec.OnFix(new Nmea.GnssFix(45.001, 9, 105, DateTimeOffset.UtcNow.AddSeconds(5), 5, 0), 8, 1.0);
        rec.Stop();
        var tr = GpxReader.ReadTracks(rec.CurrentFile!).Single();
        Assert.Equal(2, tr.Points.Count);
        Assert.Equal(105, tr.Points[1].Ele!.Value, 1);

        var cut = Path.Combine(_dir, "troncato.gpx");
        File.WriteAllText(cut, "<?xml version=\"1.0\"?>\n<gpx version=\"1.1\" xmlns=\"http://www.topografix.com/GPX/1/1\"><trk><trkseg>\n<trkpt lat=\"45\" lon=\"9\"></trkpt>\n<trkpt lat=\"45.1\" lo");
        Assert.Single(GpxReader.ReadTracks(cut).Single().Points);
    }

    [Fact]
    public void Recorder_pause_skips_points_and_resume_continues_same_file()
    {
        var rec = new GpxRecorder(_dir, minMoveMeters: 1);
        rec.Start(); var file = rec.CurrentFile;
        rec.OnFix(new Nmea.GnssFix(45, 9, null, DateTimeOffset.UtcNow, 5, 0), 8, 1);
        rec.Pause(); Assert.True(rec.IsPaused);
        rec.OnFix(new Nmea.GnssFix(46, 9, null, DateTimeOffset.UtcNow, 5, 0), 8, 1);
        rec.Resume(); Assert.False(rec.IsPaused);
        rec.OnFix(new Nmea.GnssFix(47, 9, null, DateTimeOffset.UtcNow, 5, 0), 8, 1);
        rec.Stop();
        Assert.Equal(file, rec.CurrentFile);
        Assert.Equal(2, GpxReader.ReadTracks(file!).Single().Points.Count);
    }
}
