using System.Text;

namespace GpsBridge.Core.Nmea;

/// <summary>Separa frasi NMEA e frame UBX da un unico flusso di byte.</summary>
public sealed class FrameSplitter
{
    enum St { Idle, Nmea, Sync2, Ubx }

    const int MaxNmea = 120, MaxUbxPayload = 2048;
    readonly List<byte> _buf = new();
    St _st = St.Idle;
    int _ubxTotal;

    public event Action<NmeaSentence>? Nmea;
    public event Action<byte[]>? Ubx;
    public event Action<string>? BadLine;

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) Step(b);
    }

    void Reset() { _buf.Clear(); _st = St.Idle; }

    void Step(byte b)
    {
        switch (_st)
        {
            case St.Idle:
                if (b == (byte)'$') { _buf.Add(b); _st = St.Nmea; }
                else if (b == 0xB5) { _buf.Add(b); _st = St.Sync2; }
                break;

            case St.Nmea:
                if (b == (byte)'$') { Bad(); _buf.Add(b); _st = St.Nmea; return; }
                if (b == 0xB5) { Bad(); _buf.Add(b); _st = St.Sync2; return; }
                if (b == (byte)'\n')
                {
                    string line = Encoding.ASCII.GetString(_buf.ToArray());
                    Reset();
                    if (NmeaSentence.TryParse(line, out var s)) Nmea?.Invoke(s);
                    else BadLine?.Invoke(line.TrimEnd());
                    return;
                }
                if ((b < 0x20 && b != (byte)'\r') || b > 0x7E || _buf.Count >= MaxNmea) { Bad(); Reset(); return; }
                _buf.Add(b);
                break;

            case St.Sync2:
                if (b == 0x62) { _buf.Add(b); _st = St.Ubx; _ubxTotal = 0; }
                else { Reset(); Step(b); }
                break;

            case St.Ubx:
                _buf.Add(b);
                if (_buf.Count == 6)
                {
                    int len = _buf[4] | (_buf[5] << 8);
                    if (len > MaxUbxPayload) { Reset(); return; }
                    _ubxTotal = len + 8;
                }
                if (_ubxTotal > 0 && _buf.Count == _ubxTotal)
                {
                    byte a = 0, c = 0;
                    for (int i = 2; i < _ubxTotal - 2; i++) { a += _buf[i]; c += a; }
                    var frame = _buf.ToArray();
                    Reset();
                    if (a == frame[^2] && c == frame[^1]) Ubx?.Invoke(frame);
                }
                break;
        }
    }

    void Bad()
    {
        if (_buf.Count > 0) BadLine?.Invoke(Encoding.ASCII.GetString(_buf.ToArray()));
        _buf.Clear();
    }
}
