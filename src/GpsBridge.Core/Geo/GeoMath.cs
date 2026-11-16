using System.Globalization;
using System.Text.RegularExpressions;

namespace GpsBridge.Core.Geo;

public enum CoordFormat { Decimal, Dms, Ddm, Utm }
public enum UnitSystem { Metric, Imperial, Nautical }

public static class GeoMath
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const double R = 6371000;

    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = Rad(lat2 - lat1), dLon = Rad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Rotta iniziale in gradi 0-360 (0 = nord).</summary>
    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        double p1 = Rad(lat1), p2 = Rad(lat2), dl = Rad(lon2 - lon1);
        double y = Math.Sin(dl) * Math.Cos(p2);
        double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    static readonly string[] Points = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSO", "SO", "OSO", "O", "ONO", "NO", "NNO" };
    public static string Cardinal(double deg) => Points[(int)Math.Round(((deg % 360) + 360) % 360 / 22.5) % 16];

    static double Rad(double d) => d * Math.PI / 180;

    // ---------- unita' ----------
    static string Inv2(FormattableString s) => s.ToString(Inv);

    public static string Distance(double meters, UnitSystem u)
    {
        switch (u)
        {
            case UnitSystem.Imperial:
                double ft = meters / 0.3048;
                return ft < 1000 ? Inv2($"{ft:0} ft") : Inv2($"{meters / 1609.344:0.00} mi");
            case UnitSystem.Nautical:
                return meters < 500 ? Inv2($"{meters:0} m") : Inv2($"{meters / 1852:0.00} NM");
            default:
                return meters < 1000 ? Inv2($"{meters:0} m") : Inv2($"{meters / 1000:0.00} km");
        }
    }

    public static string Speed(double? knots, UnitSystem u)
    {
        if (knots is null) return "—";
        return u switch
        {
            UnitSystem.Imperial => Inv2($"{knots.Value * 1.15078:0.0} mph"),
            UnitSystem.Nautical => Inv2($"{knots.Value:0.0} kn"),
            _ => Inv2($"{knots.Value * 1.852:0.0} km/h"),
        };
    }

    public static string Elevation(double? m, UnitSystem u) =>
        m is null ? "—" : u == UnitSystem.Imperial ? Inv2($"{m.Value / 0.3048:0} ft") : Inv2($"{m.Value:0} m");

    // ---------- formati coordinate ----------
    public static string FormatCoord(double lat, double lon, CoordFormat f) => f switch
    {
        CoordFormat.Dms => $"{Dms(lat, 'N', 'S')} {Dms(lon, 'E', 'O')}",
        CoordFormat.Ddm => $"{Ddm(lat, 'N', 'S')} {Ddm(lon, 'E', 'O')}",
        CoordFormat.Utm => Utm(lat, lon),
        _ => $"{lat.ToString("0.000000", Inv)}, {lon.ToString("0.000000", Inv)}",
    };

    static string Dms(double v, char pos, char neg)
    {
        char h = v >= 0 ? pos : neg; v = Math.Abs(v);
        int d = (int)v; double m = (v - d) * 60; int mi = (int)m; double s = (m - mi) * 60;
        if (Math.Round(s, 1) >= 60) { s = 0; mi++; }
        if (mi >= 60) { mi = 0; d++; }
        return $"{d}°{mi:00}'{s.ToString("00.0", Inv)}\"{h}";
    }

    static string Ddm(double v, char pos, char neg)
    {
        char h = v >= 0 ? pos : neg; v = Math.Abs(v);
        int d = (int)v; double m = (v - d) * 60;
        if (Math.Round(m, 3) >= 60) { m = 0; d++; }
        return $"{d}°{m.ToString("00.000", Inv)}'{h}";
    }

    /// <summary>UTM WGS84 «32T 500000 4982950» (formula di Kruger).</summary>
    public static string Utm(double lat, double lon)
    {
        if (lat < -80 || lat > 84) return "fuori UTM";
        int zone = (int)Math.Floor((lon + 180) / 6) + 1;
        if (lat >= 56 && lat < 64 && lon >= 3 && lon < 12) zone = 32;
        if (lat >= 72 && lat < 84)
            zone = lon switch { >= 0 and < 9 => 31, >= 9 and < 21 => 33, >= 21 and < 33 => 35, >= 33 and < 42 => 37, _ => zone };
        double lon0 = Rad((zone - 1) * 6 - 180 + 3);
        const double a = 6378137, f = 1 / 298.257223563, k0 = 0.9996;
        double n = f / (2 - f), n2 = n * n, n3 = n2 * n, n4 = n3 * n;
        double A = a / (1 + n) * (1 + n2 / 4 + n4 / 64);
        double[] alpha = { n / 2 - 2 * n2 / 3 + 5 * n3 / 16, 13 * n2 / 48 - 3 * n3 / 5, 61 * n3 / 240 };
        double phi = Rad(lat), lam = Rad(lon) - lon0;
        double e = Math.Sqrt(f * (2 - f));
        double t = Math.Sinh(Math.Atanh(Math.Sin(phi)) - e * Math.Atanh(e * Math.Sin(phi)));
        double xi = Math.Atan2(t, Math.Cos(lam)), eta = Math.Atanh(Math.Sin(lam) / Math.Sqrt(1 + t * t));
        double E = eta, N = xi;
        for (int j = 1; j <= 3; j++)
        {
            E += alpha[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
            N += alpha[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
        }
        double easting = 500000 + k0 * A * E, northing = k0 * A * N + (lat < 0 ? 10000000 : 0);
        const string bands = "CDEFGHJKLMNPQRSTUVWXX";
        char band = bands[(int)Math.Floor((lat + 80) / 8)];
        return $"{zone}{band} {easting:0} {northing:0}";
    }

    /// <summary>
    /// Legge coordinate scritte a mano: «45.5565, 10.2350», «45.5565 N 10.2350 E», «45°33'23.6"N 10°14'06"E»,
    /// «45°33.39'N 10°14.1'E». Gradi negativi o S/O/W = sud/ovest.
    /// </summary>
    public static bool TryParseCoord(string text, out double lat, out double lon)
    {
        lat = lon = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string t = text.Trim();
        if (t.Contains(';') && !t.Contains('.')) t = t.Replace(',', '.').Replace(';', ' ');
        t = t.Replace(';', ' ').Replace(',', ' ').Replace('°', ' ').Replace('′', ' ').Replace('″', ' ').Replace("'", " ").Replace("\"", " ");
        var tokens = Regex.Matches(t, @"-?\d+(?:\.\d+)?|[NSEWOnsewo]").Select(m => m.Value).ToList();
        var groups = new List<(List<double> nums, char hemi)>();
        var cur = new List<double>();
        foreach (var tok in tokens)
        {
            if (char.IsLetter(tok[0]))
            {
                if (cur.Count == 0) return false;
                groups.Add((cur, char.ToUpperInvariant(tok[0]))); cur = new List<double>();
            }
            else cur.Add(double.Parse(tok, Inv));
        }
        if (cur.Count > 0) groups.Add((cur, '\0'));
        if (groups.Count == 1 && groups[0].hemi == '\0' && groups[0].nums.Count is 2 or 4 or 6)
        {
            // niente lettere: meta' dei numeri a testa
            int h = groups[0].nums.Count / 2;
            groups = new() { (groups[0].nums.Take(h).ToList(), '\0'), (groups[0].nums.Skip(h).ToList(), '\0') };
        }
        if (groups.Count != 2) return false;
        double? a = Combine(groups[0].nums, groups[0].hemi), b = Combine(groups[1].nums, groups[1].hemi);
        if (a is null || b is null) return false;
        bool firstIsLon = groups[0].hemi is 'E' or 'O' or 'W' || groups[1].hemi is 'N' or 'S';
        (lat, lon) = firstIsLon ? (b.Value, a.Value) : (a.Value, b.Value);
        return Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180;
    }

    static double? Combine(List<double> n, char hemi)
    {
        if (n.Count is < 1 or > 3) return null;
        double sign = n[0] < 0 || hemi is 'S' or 'W' or 'O' ? -1 : 1;
        double v = Math.Abs(n[0]) + (n.Count > 1 ? n[1] / 60 : 0) + (n.Count > 2 ? n[2] / 3600 : 0);
        if (n.Count > 1 && n[1] >= 60 || n.Count > 2 && n[2] >= 60) return null;
        return sign * v;
    }
}
