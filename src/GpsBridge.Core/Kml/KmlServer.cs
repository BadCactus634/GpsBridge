using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using GpsBridge.Core.Nmea;

namespace GpsBridge.Core.Kml;

public static class KmlBuilder
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string N(double v) => v.ToString("0.0000000", Inv);

    /// <summary>File da aprire una volta in Google Earth: si ricarica da solo ogni secondo.</summary>
    public static string NetworkLink(int port, bool follow) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<kml xmlns=\"http://www.opengis.net/kml/2.2\" xmlns:gx=\"http://www.google.com/kml/ext/2.2\">\n" +
        " <NetworkLink>\n  <name>GpsBridge - posizione live</name>\n" +
        $"  <gx:flyToView>{(follow ? 1 : 0)}</gx:flyToView>\n" +
        $"  <Link>\n   <href>http://127.0.0.1:{port}/position.kml</href>\n" +
        "   <refreshMode>onInterval</refreshMode>\n   <refreshInterval>1</refreshInterval>\n  </Link>\n </NetworkLink>\n</kml>\n";

    public static string Position(GnssFix? fix, IReadOnlyList<(double Lat, double Lon)> track, string status, bool follow, IReadOnlyList<Waypoints.Waypoint>? waypoints = null)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<kml xmlns=\"http://www.opengis.net/kml/2.2\" xmlns:gx=\"http://www.google.com/kml/ext/2.2\">\n<Document>\n" +
            " <name>GpsBridge</name>\n" +
            " <Style id=\"p\"><IconStyle><scale>1.2</scale><Icon><href>http://maps.google.com/mapfiles/kml/shapes/arrow.png</href></Icon></IconStyle></Style>\n" +
            " <Style id=\"t\"><LineStyle><color>ff2020e5</color><width>3</width></LineStyle></Style>\n");
        if (fix is not null)
        {
            if (follow)
                sb.Append($" <LookAt><longitude>{N(fix.Lon)}</longitude><latitude>{N(fix.Lat)}</latitude><range>400</range><tilt>0</tilt></LookAt>\n");
            sb.Append($" <Placemark><name>{SecurityElement.Escape(status)}</name><styleUrl>#p</styleUrl>");
            sb.Append($"<description>{fix.Utc:yyyy-MM-dd HH:mm:ss} UTC, {(fix.SpeedKnots ?? 0) * 1.852:0.0} km/h</description>\n");
            sb.Append($"  <Point><altitudeMode>clampToGround</altitudeMode><coordinates>{N(fix.Lon)},{N(fix.Lat)},{(fix.Alt ?? 0).ToString("0.0", Inv)}</coordinates></Point></Placemark>\n");
        }
        if (track.Count > 1)
        {
            sb.Append(" <Placemark><name>Traccia</name><styleUrl>#t</styleUrl><LineString><tessellate>1</tessellate><coordinates>");
            foreach (var (lat, lon) in track) sb.Append($"{N(lon)},{N(lat)},0 ");
            sb.Append("</coordinates></LineString></Placemark>\n");
        }
        foreach (var w in waypoints ?? Array.Empty<Waypoints.Waypoint>())
        {
            sb.Append($" <Placemark><name>{SecurityElement.Escape(w.Name)}</name>");
            if (w.Notes.Length > 0) sb.Append($"<description>{SecurityElement.Escape(w.Notes)}</description>");
            sb.Append($"<Style><IconStyle><color>{Waypoints.WaypointColors.ToKml(w.Color)}</color><scale>1.1</scale><Icon><href>http://maps.google.com/mapfiles/kml/pushpin/wht-pushpin.png</href></Icon></IconStyle></Style>");
            sb.Append($"<Point><coordinates>{N(w.Lon)},{N(w.Lat)},0</coordinates></Point></Placemark>\n");
        }
        sb.Append("</Document>\n</kml>\n");
        return sb.ToString();
    }
}

/// <summary>Piccolo server HTTP solo su 127.0.0.1 che serve a Google Earth la posizione live (NetworkLink).</summary>
public sealed class KmlServer : IDisposable
{
    const int MaxTrack = 5000;
    readonly HttpListener _l = new();
    readonly object _lock = new();
    readonly List<(double, double)> _track = new();
    IReadOnlyList<Waypoints.Waypoint> _wpts = Array.Empty<Waypoints.Waypoint>();
    GnssFix? _fix; string _status = "Nessun fix";
    CancellationTokenSource? _cts;

    public int Port { get; private set; }
    public bool Follow { get; set; } = true;

    /// <param name="port">0 = porta libera scelta dal sistema.</param>
    public KmlServer(int port) { Port = port; }

    public void Start()
    {
        if (Port == 0) { var t = new TcpListener(IPAddress.Loopback, 0); t.Start(); Port = ((IPEndPoint)t.LocalEndpoint).Port; t.Stop(); }
        _l.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _l.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => Loop(_cts.Token));
    }

    public void Update(GnssFix fix, string status)
    {
        lock (_lock)
        {
            _fix = fix; _status = status;
            if (_track.Count == 0 || Geo.GeoMath.DistanceMeters(_track[^1].Item1, _track[^1].Item2, fix.Lat, fix.Lon) >= 2)
            { _track.Add((fix.Lat, fix.Lon)); if (_track.Count > MaxTrack) _track.RemoveAt(0); }
        }
    }

    public string NetworkLinkKml() => KmlBuilder.NetworkLink(Port, Follow);
    public void SetWaypoints(IReadOnlyList<Waypoints.Waypoint> w) { lock (_lock) _wpts = w; }
    public void ClearTrack() { lock (_lock) _track.Clear(); }

    async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext c;
            try { c = await _l.GetContextAsync(); } catch { return; }
            try
            {
                string? body = c.Request.Url!.AbsolutePath switch
                {
                    "/live.kml" => KmlBuilder.NetworkLink(Port, Follow),
                    "/position.kml" => Snapshot(),
                    _ => null,
                };
                if (body is null) { c.Response.StatusCode = 404; }
                else
                {
                    var b = Encoding.UTF8.GetBytes(body);
                    c.Response.ContentType = "application/vnd.google-earth.kml+xml";
                    c.Response.AddHeader("Cache-Control", "no-cache");
                    c.Response.ContentLength64 = b.Length;
                    await c.Response.OutputStream.WriteAsync(b);
                }
            }
            catch { /* client chiuso: ignorare */ }
            finally { try { c.Response.Close(); } catch { } }
        }
    }

    string Snapshot()
    {
        lock (_lock) return KmlBuilder.Position(_fix, _track.ToList(), _status, Follow, _wpts);
    }

    public void Dispose() { _cts?.Cancel(); try { _l.Close(); } catch { } }
}
