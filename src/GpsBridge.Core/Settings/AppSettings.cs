using System.Text.Json;

namespace GpsBridge.Core.Settings;

/// <summary>Opzioni del ricevitore modificabili dalla UI; ReceiverProfile.FromOptions le traduce in chiavi CFG-*.</summary>
public sealed class ReceiverOptions
{
    public bool Gps { get; set; } = true;
    public bool Galileo { get; set; } = true;
    public bool BeiDou { get; set; } = true;
    public bool Glonass { get; set; } = true;
    public bool Qzss { get; set; } = true;
    public bool Sbas { get; set; } = true;
    public int RateHz { get; set; } = 5;
    /// <summary>0 portatile, 2 fermo, 3 pedone, 4 auto, 6 aereo.</summary>
    public int DynModel { get; set; } = 0;
    /// <summary>null = non toccare il baud del ricevitore.</summary>
    public int? UartBaud { get; set; }
    public bool Gga { get; set; } = true;
    public bool Gll { get; set; } = true;
    public bool Gsa { get; set; } = true;
    public bool Gsv { get; set; } = true;
    public bool Rmc { get; set; } = true;
    public bool Vtg { get; set; } = true;
}

public sealed class AppSettings
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string? RealPort { get; set; }          // null = automatica
    public int? RealBaud { get; set; }
    /// <summary>Ultima porta/baud con cui il ricevitore ha risposto: provati per primi.</summary>
    public string? LastPort { get; set; }
    public int? LastBaud { get; set; }             // null = autorilevamento
    public bool ApplyProfileOnConnect { get; set; } = true;
    /// <summary>Google Earth via KML live su localhost (nessun driver).</summary>
    public bool KmlEnabled { get; set; } = true;
    public int KmlPort { get; set; } = 8765;
    public bool KmlFollow { get; set; } = true;
    public string GpxDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GpsBridge", "Tracce");
    public double MinMoveMeters { get; set; } = 3;
    public long TileCacheMb { get; set; } = 500;
    public bool AutoStartRecording { get; set; }
    public bool StartWithWindows { get; set; }
    public Geo.CoordFormat CoordFormat { get; set; } = Geo.CoordFormat.Decimal;
    public Geo.UnitSystem Units { get; set; } = Geo.UnitSystem.Metric;
    /// <summary>Livello mappa: osm, topo, sat.</summary>
    public string MapLayer { get; set; } = "osm";
    /// <summary>Tema dell'interfaccia: dark, light, auto (segue Windows).</summary>
    public string Theme { get; set; } = "auto";
    public ReceiverOptions Receiver { get; set; } = new();

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GpsBridge", "settings.json");

    public static AppSettings Load(string path)
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings(); }
        catch { return new AppSettings(); }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}

