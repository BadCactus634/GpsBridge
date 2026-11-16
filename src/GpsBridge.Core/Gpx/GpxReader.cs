using System.Globalization;
using System.Xml.Linq;
using GpsBridge.Core.Geo;

namespace GpsBridge.Core.Gpx;

public sealed record TrackPoint(double Lat, double Lon, double? Ele, DateTimeOffset? Time);
public sealed record GpxTrack(string Name, IReadOnlyList<TrackPoint> Points);

public sealed record TrackStats(int Points, double DistanceM, TimeSpan? Duration, TimeSpan? MovingTime,
    double? AvgSpeedKnots, double? MaxSpeedKnots, double? AscentM, double? DescentM, double? MinEle, double? MaxEle);

public static class GpxReader
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Legge le tracce di un GPX anche se troncato (file di una registrazione interrotta).</summary>
    public static List<GpxTrack> ReadTracks(string path)
    {
        string text = File.ReadAllText(path);
        if (!text.Contains("</gpx>")) { GpxRecorder.RepairIfOpen(path); text = File.ReadAllText(path); }
        var d = XDocument.Parse(text);
        var r = new List<GpxTrack>();
        foreach (var trk in d.Descendants().Where(x => x.Name.LocalName == "trk"))
        {
            var pts = new List<TrackPoint>();
            foreach (var p in trk.Descendants().Where(x => x.Name.LocalName == "trkpt"))
            {
                if (!double.TryParse((string?)p.Attribute("lat"), NumberStyles.Float, Inv, out var lat) ||
                    !double.TryParse((string?)p.Attribute("lon"), NumberStyles.Float, Inv, out var lon)) continue;
                string? C(string n) => p.Elements().FirstOrDefault(x => x.Name.LocalName == n)?.Value;
                pts.Add(new TrackPoint(lat, lon,
                    double.TryParse(C("ele"), NumberStyles.Float, Inv, out var e) ? e : null,
                    DateTimeOffset.TryParse(C("time"), Inv, DateTimeStyles.AssumeUniversal, out var t) ? t : null));
            }
            r.Add(new GpxTrack(trk.Elements().FirstOrDefault(x => x.Name.LocalName == "name")?.Value ?? Path.GetFileNameWithoutExtension(path), pts));
        }
        return r;
    }

    /// <summary>Statistiche di viaggio. Quota: le variazioni sotto 3 m non contano (rumore del GPS). In movimento = velocita' &gt; 0,5 km/h.</summary>
    public static TrackStats Stats(IReadOnlyList<TrackPoint> pts)
    {
        double dist = 0, up = 0, down = 0, maxSpeed = 0; var moving = TimeSpan.Zero;
        double? refEle = null, minE = null, maxE = null;
        for (int i = 0; i < pts.Count; i++)
        {
            if (pts[i].Ele is { } e)
            {
                minE = minE is null ? e : Math.Min(minE.Value, e); maxE = maxE is null ? e : Math.Max(maxE.Value, e);
                if (refEle is null) refEle = e;
                else if (Math.Abs(e - refEle.Value) >= 3) { if (e > refEle) up += e - refEle.Value; else down += refEle.Value - e; refEle = e; }
            }
            if (i == 0) continue;
            double d = GeoMath.DistanceMeters(pts[i - 1].Lat, pts[i - 1].Lon, pts[i].Lat, pts[i].Lon);
            dist += d;
            if (pts[i].Time is { } t1 && pts[i - 1].Time is { } t0 && t1 > t0)
            {
                double dt = (t1 - t0).TotalSeconds, v = d / dt;           // m/s
                if (dt <= 60 && v > 0.14) moving += t1 - t0;
                if (dt <= 60 && v < 100) maxSpeed = Math.Max(maxSpeed, v);
            }
        }
        TimeSpan? dur = pts.Count > 1 && pts[0].Time is { } a && pts[^1].Time is { } b ? b - a : null;
        double? avg = moving.TotalSeconds > 0 ? dist / moving.TotalSeconds / 0.514444 : null;
        return new TrackStats(pts.Count, dist, dur, dur is null ? null : moving, avg, dur is null ? null : maxSpeed / 0.514444,
            minE is null ? null : up, minE is null ? null : down, minE, maxE);
    }
}
