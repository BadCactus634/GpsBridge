using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace GpsBridge.Core.Waypoints;

public sealed record Waypoint(Guid Id, string Name, double Lat, double Lon, double? Ele, string Color, string Notes, DateTimeOffset Created)
{
    public static Waypoint Create(string name, double lat, double lon, double? ele = null, string color = "#E53935", string notes = "") =>
        new(Guid.NewGuid(), string.IsNullOrWhiteSpace(name) ? "Punto" : name.Trim(), lat, lon, ele, color, notes, DateTimeOffset.UtcNow);
}

public static class WaypointColors
{
    public static readonly IReadOnlyList<(string Name, string Hex)> Palette = new[]
    {
        ("Rosso", "#E53935"), ("Arancione", "#FB8C00"), ("Giallo", "#FDD835"), ("Verde", "#43A047"),
        ("Azzurro", "#29B6F6"), ("Blu", "#1E4FD8"), ("Viola", "#8E24AA"), ("Rosa", "#EC407A"),
        ("Marrone", "#6D4C41"), ("Nero", "#212121"), ("Bianco", "#FAFAFA"),
    };

    /// <summary>"#RRGGBB" -> colore KML aabbggrr (opaco).</summary>
    public static string ToKml(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) hex = "E53935";
        return ("ff" + hex.Substring(4, 2) + hex.Substring(2, 2) + hex.Substring(0, 2)).ToLowerInvariant();
    }
}

/// <summary>Elenco dei punti salvati (JSON), con export/import GPX 1.1. path = null: solo in memoria.</summary>
public sealed class WaypointStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly XNamespace G = "http://www.topografix.com/GPX/1/1";
    static readonly XNamespace Ext = "http://github.com/gpsbridge/1";

    readonly string? _path;
    readonly List<Waypoint> _items = new();
    readonly object _lock = new();

    public event Action? Changed;

    public WaypointStore(string? path)
    {
        _path = path;
        if (path is not null && File.Exists(path))
        {
            try { _items.AddRange(JsonSerializer.Deserialize<List<Waypoint>>(File.ReadAllText(path)) ?? new()); }
            catch { /* file rovinato: si riparte vuoti, l'originale resta finche' non si salva */ }
        }
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GpsBridge", "waypoints.json");

    public IReadOnlyList<Waypoint> Items { get { lock (_lock) return _items.ToList(); } }

    public Waypoint Add(Waypoint w) { lock (_lock) _items.Add(w); Save(); return w; }

    public bool Update(Waypoint w)
    {
        lock (_lock)
        {
            int i = _items.FindIndex(x => x.Id == w.Id);
            if (i < 0) return false;
            _items[i] = w;
        }
        Save(); return true;
    }

    public bool Remove(Guid id)
    {
        bool ok; lock (_lock) ok = _items.RemoveAll(x => x.Id == id) > 0;
        if (ok) Save();
        return ok;
    }

    void Save()
    {
        if (_path is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Items, Json));
                File.Move(tmp, _path, true);
            }
            catch { /* disco pieno o file bloccato: i punti restano in memoria */ }
        }
        Changed?.Invoke();
    }

    // ---------- GPX ----------
    public static XDocument ToGpx(IEnumerable<Waypoint> items)
    {
        var root = new XElement(G + "gpx", new XAttribute("version", "1.1"), new XAttribute("creator", "GpsBridge"),
            new XAttribute(XNamespace.Xmlns + "gb", Ext.NamespaceName));
        foreach (var w in items)
        {
            var e = new XElement(G + "wpt", new XAttribute("lat", w.Lat.ToString("0.0000000", Inv)), new XAttribute("lon", w.Lon.ToString("0.0000000", Inv)));
            if (w.Ele is { } el) e.Add(new XElement(G + "ele", el.ToString("0.0", Inv)));
            e.Add(new XElement(G + "time", w.Created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", Inv)));
            e.Add(new XElement(G + "name", w.Name));
            if (w.Notes.Length > 0) e.Add(new XElement(G + "desc", w.Notes));
            e.Add(new XElement(G + "extensions", new XElement(Ext + "color", w.Color)));
            root.Add(e);
        }
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    public void ExportGpx(string path) => ToGpx(Items).Save(path);

    public static List<Waypoint> ParseGpx(XDocument d)
    {
        var r = new List<Waypoint>();
        foreach (var e in d.Descendants().Where(x => x.Name.LocalName == "wpt"))
        {
            if (!double.TryParse((string?)e.Attribute("lat"), NumberStyles.Float, Inv, out var lat) ||
                !double.TryParse((string?)e.Attribute("lon"), NumberStyles.Float, Inv, out var lon)) continue;
            string? Child(string n) => e.Elements().FirstOrDefault(x => x.Name.LocalName == n)?.Value;
            double? ele = double.TryParse(Child("ele"), NumberStyles.Float, Inv, out var el) ? el : null;
            string color = e.Descendants().FirstOrDefault(x => x.Name.LocalName == "color")?.Value ?? "#E53935";
            var created = DateTimeOffset.TryParse(Child("time"), Inv, DateTimeStyles.AssumeUniversal, out var t) ? t : DateTimeOffset.UtcNow;
            r.Add(new Waypoint(Guid.NewGuid(), Child("name") ?? "Punto", lat, lon, ele, color, Child("desc") ?? "", created));
        }
        return r;
    }

    /// <summary>Aggiunge i punti di un GPX; salta quelli gia' presenti (stesso nome e posizione entro 1 m). Restituisce quanti ne ha aggiunti.</summary>
    public int ImportGpx(string path)
    {
        int n = 0;
        foreach (var w in ParseGpx(XDocument.Load(path)))
        {
            bool dup;
            lock (_lock) dup = _items.Any(x => x.Name == w.Name && Geo.GeoMath.DistanceMeters(x.Lat, x.Lon, w.Lat, w.Lon) < 1);
            if (dup) continue;
            lock (_lock) _items.Add(w);
            n++;
        }
        if (n > 0) Save();
        return n;
    }
}
