using System.Xml.Linq;
using GpsBridge.Core.Gpx;
using GpsBridge.Core.Nmea;
using Xunit;

namespace GpsBridge.Core.Tests;

public class GpxRecorderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "gpsb-gpx-" + Guid.NewGuid().ToString("N"));
    public GpxRecorderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static GnssFix Fix(double lat, double lon, double? speed = 0) =>
        new(lat, lon, 100, new DateTimeOffset(2026, 1, 16, 12, 0, 0, TimeSpan.Zero), speed, null);

    static XDocument Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return XDocument.Load(fs);
    }
    static int Points(XDocument d) => d.Descendants().Count(e => e.Name.LocalName == "trkpt");

    [Fact]
    public void Stationary_points_are_dropped_and_moving_ones_kept()
    {
        var r = new GpxRecorder(_dir, minMoveMeters: 3);
        r.Start();
        r.OnFix(Fix(45.0, 9.0), 8, 0.9);
        r.OnFix(Fix(45.000005, 9.0), 8, 0.9);   // ~0,5 m, fermo
        r.OnFix(Fix(45.0002, 9.0), 8, 0.9);     // ~22 m
        r.Stop();
        Assert.Equal(2, Points(Load(r.CurrentFile!)));
    }

    [Fact]
    public void File_is_valid_gpx_while_recording_without_stop()
    {
        var r = new GpxRecorder(_dir);
        r.Start();
        r.OnFix(Fix(45.0, 9.0), 8, 0.9);
        r.OnFix(Fix(45.01, 9.0), 9, 1.1);
        var d = Load(r.CurrentFile!);               // chiusura anomala simulata: niente Stop
        Assert.Equal(2, Points(d));
        Assert.Contains(d.Descendants(), e => e.Name.LocalName == "sat" && e.Value == "9");
        r.Stop();
    }

    [Fact]
    public void No_fix_means_no_points_but_valid_file()
    {
        var r = new GpxRecorder(_dir);
        r.Start();
        Assert.True(r.IsRecording);
        r.Stop();
        Assert.False(r.IsRecording);
        Assert.Equal(0, Points(Load(r.CurrentFile!)));
    }

    [Fact]
    public void Repair_closes_a_truncated_file()
    {
        var path = Path.Combine(_dir, "troncato.gpx");
        File.WriteAllText(path,
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<gpx version=\"1.1\" creator=\"GpsBridge\" xmlns=\"http://www.topografix.com/GPX/1/1\">\n" +
            " <trk><trkseg>\n<trkpt lat=\"45\" lon=\"9\"><time>2026-01-16T12:00:00Z</time></trkpt>\n<trkpt lat=\"45.1\" lo");
        Assert.True(GpxRecorder.RepairIfOpen(path));
        Assert.Equal(1, Points(Load(path)));
        Assert.False(GpxRecorder.RepairIfOpen(path));   // gia' chiuso: nulla da fare
    }
}
