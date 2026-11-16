using System.Globalization;

namespace GpsBridge.Core.Nmea;

public sealed record GnssFix(double Lat, double Lon, double? Alt, DateTimeOffset Utc, double? SpeedKnots, double? CourseDeg);
public sealed record SatInfo(string Constellation, int Prn, int? Elev, int? Azim, int? Snr, bool Used);

/// <summary>Stato del ricevitore ricostruito da GGA/RMC/GSA/GSV (qualsiasi talker).</summary>
public sealed class GnssState
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly object _l = new();
    double? _lat, _lon, _alt;
    TimeSpan? _time;
    DateTime? _date;
    char _rmcStatus = '?';
    double? _speed, _course;

    // GSV: ultimo insieme completo per (costellazione, signalId); parziali in costruzione
    readonly Dictionary<(string, string), List<SatInfo>> _committed = new();
    readonly Dictionary<(string, string), List<SatInfo>> _pending = new();
    // GSA: PRN usati per costellazione; chiave "?" = GSA senza system id (talker GN)
    readonly Dictionary<string, HashSet<int>> _used = new();

    public int FixQuality { get; private set; }
    public double? Hdop { get; private set; }
    public double? Pdop { get; private set; }
    public double? Vdop { get; private set; }
    public event Action? Changed;

    public GnssFix? Position
    {
        get
        {
            lock (_l) {
            if (FixQuality <= 0 || _rmcStatus == 'V' || _lat is null || _lon is null || _time is null) return null;
            var date = _date ?? DateTime.UtcNow.Date;
            var utc = new DateTimeOffset(date.Add(_time.Value), TimeSpan.Zero);
            return new GnssFix(_lat.Value, _lon.Value, _alt, utc, _speed, _course);
            }
        }
    }

    public IReadOnlyList<SatInfo> Satellites
    {
        get
        {
            lock (_l) {
            var best = new Dictionary<(string, int), SatInfo>();
            foreach (var list in _committed.Values)
                foreach (var s in list)
                {
                    bool used = IsUsed(s.Constellation, s.Prn);
                    var sat = s with { Used = used };
                    if (!best.TryGetValue((s.Constellation, s.Prn), out var old) || (sat.Snr ?? -1) > (old.Snr ?? -1))
                        best[(s.Constellation, s.Prn)] = sat;
                }
            return best.Values.OrderBy(s => s.Constellation).ThenBy(s => s.Prn).ToList();
            }
        }
    }

    public IReadOnlyDictionary<string, (int Used, int InView)> PerConstellation =>
        Satellites.GroupBy(s => s.Constellation)
                  .ToDictionary(g => g.Key, g => (g.Count(x => x.Used), g.Count()));

    bool IsUsed(string constellation, int prn) =>
        (_used.TryGetValue(constellation, out var a) && a.Contains(prn)) ||
        (_used.TryGetValue("?", out var b) && b.Contains(prn));

    public void Apply(NmeaSentence s)
    {
        lock (_l)
        {
            switch (s.Type)
            {
                case "GGA": Gga(s.Fields); break;
                case "RMC": Rmc(s.Fields); break;
                case "GSA": Gsa(s); break;
                case "GSV": Gsv(s); break;
                default: return;
            }
        }
        Changed?.Invoke();
    }

    static double? D(string[] f, int i) =>
        i < f.Length && double.TryParse(f[i], NumberStyles.Float, Inv, out var v) ? v : null;
    static int? I(string[] f, int i) =>
        i < f.Length && int.TryParse(f[i], NumberStyles.Integer, Inv, out var v) ? v : null;

    static double? Coord(string[] f, int v, int h)
    {
        var raw = D(f, v);
        if (raw is null || h >= f.Length || f[h].Length == 0) return null;
        double deg = Math.Floor(raw.Value / 100);
        double dd = deg + (raw.Value - deg * 100) / 60.0;
        return f[h] is "S" or "W" ? -dd : dd;
    }

    static TimeSpan? Time(string[] f, int i)
    {
        if (i >= f.Length || f[i].Length < 6) return null;
        if (!int.TryParse(f[i].AsSpan(0, 2), out var h) || !int.TryParse(f[i].AsSpan(2, 2), out var m)) return null;
        if (!double.TryParse(f[i].AsSpan(4), NumberStyles.Float, Inv, out var sec)) return null;
        return new TimeSpan(0, h, m, 0).Add(TimeSpan.FromSeconds(sec));
    }

    void Gga(string[] f)
    {
        FixQuality = I(f, 5) ?? 0;
        _time = Time(f, 0) ?? _time;
        var lat = Coord(f, 1, 2); var lon = Coord(f, 3, 4);
        if (lat is not null && lon is not null) { _lat = lat; _lon = lon; }
        _alt = D(f, 8);
        Hdop = D(f, 7) is { } h && FixQuality > 0 ? h : Hdop;
        if (FixQuality == 0) { _alt = null; }
    }

    void Rmc(string[] f)
    {
        _time = Time(f, 0) ?? _time;
        _rmcStatus = f.Length > 1 && f[1].Length > 0 ? f[1][0] : '?';
        if (f.Length > 8 && f[8].Length == 6 &&
            DateTime.TryParseExact(f[8], "ddMMyy", Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d))
            _date = d.Date;
        if (_rmcStatus == 'A')
        {
            var lat = Coord(f, 2, 3); var lon = Coord(f, 4, 5);
            if (lat is not null && lon is not null) { _lat = lat; _lon = lon; }
            _speed = D(f, 6); _course = D(f, 7);
        }
        else { _speed = null; _course = null; }
    }

    static string? FromTalker(string t) => t switch
    {
        "GP" => "GPS", "GL" => "GLONASS", "GA" => "Galileo", "GB" or "BD" => "BeiDou", "GQ" => "QZSS", _ => null
    };
    static string? FromSystemId(int? id) => id switch
    {
        1 => "GPS", 2 => "GLONASS", 3 => "Galileo", 4 => "BeiDou", 5 => "QZSS", _ => null
    };

    void Gsa(NmeaSentence s)
    {
        var f = s.Fields;
        Pdop = D(f, 14) ?? Pdop; Hdop = D(f, 15) ?? Hdop; Vdop = D(f, 16) ?? Vdop;
        string key = FromSystemId(I(f, 17)) ?? FromTalker(s.Talker) ?? "?";
        var set = new HashSet<int>();
        for (int i = 2; i <= 13 && i < f.Length; i++) if (I(f, i) is { } prn) set.Add(prn);
        _used[key] = set;
    }

    void Gsv(NmeaSentence s)
    {
        var f = s.Fields;
        string? c = FromTalker(s.Talker);
        if (c is null || f.Length < 3) return;
        int total = I(f, 0) ?? 1, num = I(f, 1) ?? 1;
        int n = (f.Length - 3) / 4;
        string signal = (f.Length - 3) % 4 == 1 ? f[^1] : "";
        var key = (c, signal);
        if (num == 1 || !_pending.ContainsKey(key)) _pending[key] = new List<SatInfo>();
        for (int g = 0; g < n; g++)
        {
            int b = 3 + g * 4;
            if (I(f, b) is not { } prn) continue;
            _pending[key].Add(new SatInfo(c, prn, I(f, b + 1), I(f, b + 2), I(f, b + 3), false));
        }
        if (num == total) { _committed[key] = _pending[key]; _pending.Remove(key); }
    }
}
