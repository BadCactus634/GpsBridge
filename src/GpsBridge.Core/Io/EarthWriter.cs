using System.Diagnostics;
using System.Text;
using GpsBridge.Core.Nmea;

namespace GpsBridge.Core.Io;

public sealed record EarthStatus(bool Available, string? Error, long Sentences);

/// <summary>
/// Prepara l'NMEA per Google Earth: solo frasi che Earth capisce, talker GP, GSA a 17 campi,
/// al massimo <c>hz</c> epoche al secondo (un'epoca = tutte le frasi dello stesso istante).
/// </summary>
public sealed class EarthWriter
{
    static readonly HashSet<string> Allowed = new() { "GGA", "RMC", "GSA", "GSV", "VTG", "GLL" };
    static readonly HashSet<string> EpochStarters = new() { "GGA", "RMC", "VTG", "GLL" };

    readonly IByteStream _out;
    readonly TimeSpan _interval;
    readonly Func<DateTime> _clock;
    readonly HashSet<string> _seen = new();
    DateTime? _lastEpoch;
    bool _passing = true;
    long _count;

    public EarthStatus Status { get; private set; } = new(true, null, 0);

    public EarthWriter(IByteStream outPort, double hz = 1, Func<DateTime>? clock = null)
    {
        _out = outPort;
        _interval = TimeSpan.FromSeconds(1.0 / Math.Max(hz, 0.1));
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public void OnSentence(NmeaSentence s)
    {
        if (!Allowed.Contains(s.Type)) return;

        if (EpochStarters.Contains(s.Type) && !_seen.Add(s.Type))
        {
            _seen.Clear(); _seen.Add(s.Type);
            _passing = Admit();
        }
        else if (_lastEpoch is null) _passing = Admit();

        if (!_passing) return;

        var outSentence = s.Type == "GSA" && s.Fields.Length > 17 ? s with { Fields = s.Fields[..17] } : s;
        try
        {
            _out.Write(Encoding.ASCII.GetBytes(outSentence.WithTalker("GP")));
            Status = new EarthStatus(true, null, ++_count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            Status = new EarthStatus(false, "COM virtuale non disponibile: " + e.Message, _count);
        }
    }

    bool Admit()
    {
        var now = _clock();
        if (_lastEpoch is { } last && now - last < _interval) return false;
        _lastEpoch = now;
        return true;
    }
}
