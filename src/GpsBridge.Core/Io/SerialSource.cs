using System.IO.Ports;
using GpsBridge.Core.Nmea;

namespace GpsBridge.Core.Io;

public interface IByteStream
{
    void Write(ReadOnlySpan<byte> data);
    event Action<byte[]>? DataReceived;
}

/// <summary>Porta seriale astratta. Read: n&gt;0 byte, 0 = timeout senza dati, eccezione = porta persa.</summary>
public interface IPort : IDisposable
{
    void Open();
    int Read(byte[] buf, int off, int count);
    void Write(byte[] buf, int off, int count);
}

/// <summary>Sorgente di byte che gira in un ciclo proprio (porta reale o file di prova).</summary>
public interface IRunnableSource : IByteStream
{
    Task RunAsync(CancellationToken ct);
}

public enum ConnState { Disconnected, Connecting, Connected }

public sealed class SerialPortAdapter : IPort
{
    readonly SerialPort _p;
    public SerialPortAdapter(string name, int baud) =>
        _p = new SerialPort(name, baud, Parity.None, 8, StopBits.One) { ReadTimeout = 500, WriteTimeout = 1000, DtrEnable = true };
    public void Open() => _p.Open();
    public int Read(byte[] buf, int off, int count)
    {
        try { return _p.Read(buf, off, count); }
        catch (TimeoutException) { return 0; }
    }
    public void Write(byte[] buf, int off, int count) => _p.Write(buf, off, count);
    public void Dispose() { try { _p.Dispose(); } catch { } }
    public static IEnumerable<string> List() => SerialPort.GetPortNames().OrderBy(x => x.Length).ThenBy(x => x);
}

/// <summary>Porta reale con autorilevamento del baud e riconnessione automatica.</summary>
public sealed class SerialSource : IRunnableSource
{
    public static readonly int[] CandidateBauds = { 115200, 38400, 9600, 4800, 230400 };

    readonly Func<string, int, IPort> _opener;
    readonly Func<IEnumerable<string>> _listPorts;
    readonly string? _prefPort; readonly int? _prefBaud;
    readonly string? _fixedPort;
    readonly int? _fixedBaud;
    readonly TimeSpan _probe;
    readonly int[] _backoffMs;
    volatile IPort? _current;
    ConnState _state = ConnState.Disconnected;

    public event Action<byte[]>? DataReceived;
    public event Action<ConnState>? StateChanged;
    public int DetectedBaud { get; private set; }
    public string? PortName { get; private set; }
    public int Reconnects { get; private set; }
    public string? LastGoodPort { get; private set; }
    public int? LastGoodBaud { get; private set; }
    public ConnState State => _state;

    public SerialSource(Func<string, int, IPort> opener, Func<IEnumerable<string>> listPorts,
        string? fixedPort = null, int? fixedBaud = null, TimeSpan? probe = null, int[]? backoffMs = null,
        string? preferredPort = null, int? preferredBaud = null)
    {
        _prefPort = preferredPort; _prefBaud = preferredBaud; _opener = opener; _listPorts = listPorts; _fixedPort = fixedPort; _fixedBaud = fixedBaud;
        _probe = probe ?? TimeSpan.FromMilliseconds(1500);
        _backoffMs = backoffMs ?? new[] { 1000, 2000, 5000, 10000 };
    }

    void Set(ConnState s)
    {
        if (_state == s) return;
        _state = s; StateChanged?.Invoke(s);
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var p = _current;
        if (p is null) return;
        try { p.Write(data.ToArray(), 0, data.Length); } catch { /* la perdita si vede nel ciclo di lettura */ }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            bool connected = await Task.Run(() => TryConnectAndRead(ct), CancellationToken.None);
            Set(ConnState.Disconnected);
            if (ct.IsCancellationRequested) break;
            attempt = connected ? 0 : attempt + 1;
            if (connected) Reconnects++;
            int wait = _backoffMs[Math.Min(Math.Max(attempt - 1, 0), _backoffMs.Length - 1)];
            try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>true se si e' connessa almeno una volta prima di perdere la porta.</summary>
    bool TryConnectAndRead(CancellationToken ct)
    {
        Set(ConnState.Connecting);
        IEnumerable<string> ports;
        try { ports = _fixedPort is not null ? new[] { _fixedPort } : _listPorts().OrderBy(p => p == (LastGoodPort ?? _prefPort) ? 0 : 1).ToList(); }
        catch { return false; }

        foreach (var name in ports)
            foreach (int baud in _fixedBaud is { } fb ? new[] { fb } : CandidateBauds.OrderBy(b => b == (LastGoodBaud ?? _prefBaud) ? 0 : 1).ToArray())
            {
                if (ct.IsCancellationRequested) return false;
                IPort? port = null;
                try
                {
                    port = _opener(name, baud);
                    port.Open();
                    if (!Probe(port, ct, out var first)) { port.Dispose(); continue; }
                    _current = port; PortName = name; DetectedBaud = baud; LastGoodPort = name; LastGoodBaud = baud;
                    Set(ConnState.Connected);
                    foreach (var c in first) DataReceived?.Invoke(c);
                    ReadLoop(port, ct);
                    return true;
                }
                catch { /* apertura o lettura fallita: prossimo tentativo */ }
                finally
                {
                    if (_current == port) _current = null;
                    port?.Dispose();
                }
            }
        return false;
    }

    bool Probe(IPort port, CancellationToken ct, out List<byte[]> chunks)
    {
        chunks = new List<byte[]>();
        var fs = new FrameSplitter(); int good = 0;
        fs.Nmea += _ => good++;
        var buf = new byte[512];
        var end = DateTime.UtcNow + _probe;
        while (DateTime.UtcNow < end && !ct.IsCancellationRequested)
        {
            int n = port.Read(buf, 0, buf.Length);
            if (n <= 0) continue;
            chunks.Add(buf.AsSpan(0, n).ToArray());
            fs.Feed(buf.AsSpan(0, n));
            if (good >= 2) return true;
        }
        return false;
    }

    void ReadLoop(IPort port, CancellationToken ct)
    {
        var buf = new byte[1024];
        while (!ct.IsCancellationRequested)
        {
            int n = port.Read(buf, 0, buf.Length);   // eccezione = porta persa
            if (n > 0) DataReceived?.Invoke(buf.AsSpan(0, n).ToArray());
        }
    }
}

/// <summary>Riproduce un file NMEA come se fosse un ricevitore (prove senza hardware, --replay).</summary>
public sealed class ReplayStream : IRunnableSource
{
    readonly string _path; readonly int _delay; readonly bool _loop;
    public event Action<byte[]>? DataReceived;
    public ReplayStream(string path, int lineDelayMs = 20, bool loop = true) { _path = path; _delay = lineDelayMs; _loop = loop; }
    public void Write(ReadOnlySpan<byte> data) { }

    public async Task RunAsync(CancellationToken ct)
    {
        var lines = File.ReadAllLines(_path);
        do
        {
            foreach (var l in lines)
            {
                if (ct.IsCancellationRequested) return;
                DataReceived?.Invoke(System.Text.Encoding.ASCII.GetBytes(l + "\r\n"));
                try { await Task.Delay(_delay, ct); } catch (OperationCanceledException) { return; }
            }
        } while (_loop && !ct.IsCancellationRequested);
    }
}
