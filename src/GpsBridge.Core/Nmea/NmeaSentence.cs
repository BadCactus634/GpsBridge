using System.Diagnostics.CodeAnalysis;

namespace GpsBridge.Core.Nmea;

/// <summary>Frase NMEA 0183 validata. Fields esclude l'indirizzo (es. "GNGGA").</summary>
public sealed record NmeaSentence(string Talker, string Type, string[] Fields, string Raw)
{
    public static string Checksum(string body)
    {
        int x = 0;
        foreach (char c in body) x ^= c;
        return x.ToString("X2");
    }

    public static bool TryParse(string line, [NotNullWhen(true)] out NmeaSentence? s)
    {
        s = null;
        line = line.Trim('\r', '\n', ' ');
        if (line.Length < 8 || line[0] != '$') return false;
        int star = line.LastIndexOf('*');
        if (star < 0 || star != line.Length - 3) return false;
        string body = line.Substring(1, star - 1);
        if (!string.Equals(Checksum(body), line.Substring(star + 1), StringComparison.OrdinalIgnoreCase)) return false;
        var parts = body.Split(',');
        string addr = parts[0];
        if (addr.Length < 3) return false;
        string talker, type;
        if (addr[0] == 'P') { talker = "P"; type = addr.Substring(1); }   // proprietarie (PUBX...)
        else { talker = addr.Substring(0, 2); type = addr.Substring(2); }
        s = new NmeaSentence(talker, type, parts.Skip(1).ToArray(), line);
        return true;
    }

    /// <summary>Riga con talker sostituito e checksum ricalcolato, terminata da CRLF.</summary>
    public string WithTalker(string talker)
    {
        string body = talker + Type + (Fields.Length > 0 ? "," + string.Join(",", Fields) : "");
        return $"${body}*{Checksum(body)}\r\n";
    }
}
