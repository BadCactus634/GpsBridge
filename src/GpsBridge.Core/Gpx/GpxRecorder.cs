using System.Globalization;
using System.Text;
using GpsBridge.Core.Nmea;

namespace GpsBridge.Core.Gpx;

/// <summary>
/// Registratore GPX 1.1. Il file e' sempre un GPX valido: il «piede» (chiusura dei tag) sta in coda
/// e viene sovrascritto da ogni nuovo punto, quindi una chiusura anomala non lascia file rotti.
/// </summary>
public sealed class GpxRecorder
{
    const string Footer = "  </trkseg>\n </trk>\n</gpx>\n";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly string _dir;
    readonly double _minMove, _minSpeed;
    FileStream? _fs;
    (double Lat, double Lon)? _last;
    readonly object _lock = new();

    public bool IsRecording => _fs is not null;
    /// <summary>In pausa il file resta aperto ma i punti non vengono scritti; Resume() continua nello stesso file.</summary>
    public bool IsPaused { get; private set; }
    public void Pause() { lock (_lock) { if (_fs is not null) IsPaused = true; } }
    public void Resume() { lock (_lock) { IsPaused = false; _last = null; } }
    public string? CurrentFile { get; private set; }
    public int Points { get; private set; }
    readonly List<TrackPoint> _pts = new();
    /// <summary>Copia dei punti scritti finora (per statistiche e profilo altimetrico in tempo reale).</summary>
    public IReadOnlyList<TrackPoint> Snapshot() { lock (_lock) return _pts.ToList(); }

    public GpxRecorder(string dir, double minMoveMeters = 3, double minSpeedKnots = 0.3)
    {
        _dir = dir; _minMove = minMoveMeters; _minSpeed = minSpeedKnots;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_fs is not null) return;
            Directory.CreateDirectory(_dir);
            CurrentFile = Path.Combine(_dir, $"traccia-{DateTime.Now:yyyyMMdd-HHmmss}.gpx");
            _fs = new FileStream(CurrentFile, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            var name = Path.GetFileNameWithoutExtension(CurrentFile);
            WriteRaw("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                     "<gpx version=\"1.1\" creator=\"GpsBridge\" xmlns=\"http://www.topografix.com/GPX/1/1\">\n" +
                     $" <trk>\n  <name>{name}</name>\n  <trkseg>\n" + Footer);
            _last = null; Points = 0; _pts.Clear();
        }
    }

    public void Stop()
    {
        lock (_lock) { _fs?.Dispose(); _fs = null; IsPaused = false; }
    }

    void WriteRaw(string text)
    {
        var b = Encoding.UTF8.GetBytes(text);
        _fs!.Write(b, 0, b.Length);
        _fs.Flush();
    }

    public void OnFix(GnssFix f, int sats, double? hdop)
    {
        lock (_lock)
        {
            if (_fs is null || IsPaused) return;
            if (_last is { } l && Geo.GeoMath.DistanceMeters(l.Lat, l.Lon, f.Lat, f.Lon) < _minMove && (f.SpeedKnots ?? 0) < _minSpeed) return;
            var sb = new StringBuilder();
            sb.Append($"   <trkpt lat=\"{f.Lat.ToString("0.0000000", Inv)}\" lon=\"{f.Lon.ToString("0.0000000", Inv)}\">");
            if (f.Alt is { } a) sb.Append($"<ele>{a.ToString("0.0", Inv)}</ele>");
            sb.Append($"<time>{f.Utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", Inv)}</time>");
            sb.Append($"<sat>{sats}</sat>");
            if (hdop is { } h) sb.Append($"<hdop>{h.ToString("0.0#", Inv)}</hdop>");
            sb.Append("</trkpt>\n");
            _fs.Seek(-Encoding.UTF8.GetByteCount(Footer), SeekOrigin.End);
            WriteRaw(sb.ToString() + Footer);
            _last = (f.Lat, f.Lon); Points++;
            if (_pts.Count < 500_000) _pts.Add(new TrackPoint(f.Lat, f.Lon, f.Alt, f.Utc));
        }
    }

    /// <summary>Chiude un file rimasto troncato (senza &lt;/gpx&gt;). true se ha riparato qualcosa.</summary>
    public static bool RepairIfOpen(string path)
    {
        var text = File.ReadAllText(path);
        if (text.Contains("</gpx>")) return false;
        int idx = text.LastIndexOf("</trkpt>", StringComparison.Ordinal);
        int cut;
        if (idx >= 0) cut = idx + "</trkpt>".Length;
        else { int s = text.IndexOf("<trkseg>", StringComparison.Ordinal); if (s < 0) return false; cut = s + "<trkseg>".Length; }
        File.WriteAllText(path, text[..cut] + "\n" + Footer, new UTF8Encoding(false));
        return true;
    }
}
