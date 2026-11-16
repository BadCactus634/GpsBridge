using System.Collections.Concurrent;

namespace GpsBridge.Core.Io;

/// <summary>
/// Scrittura su una porta (la COM virtuale per Earth) con un thread proprio: una porta bloccata o assente
/// non ferma mai il flusso del ricevitore. Riprova ad aprire; l'ultimo errore si legge in Write() come IOException.
/// </summary>
public sealed class PortWriter : IByteStream, IDisposable
{
    readonly Func<IPort> _opener;
    readonly BlockingCollection<byte[]> _q = new(200);
    readonly Thread _t;
    volatile string? _error;
    IPort? _port;
    DateTime _nextTry = DateTime.MinValue;

    public event Action<byte[]>? DataReceived { add { } remove { } }

    public PortWriter(Func<IPort> opener)
    {
        _opener = opener;
        _t = new Thread(Loop) { IsBackground = true, Name = "EarthPortWriter" };
        _t.Start();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        _q.TryAdd(data.ToArray());            // coda piena = si scarta, mai bloccare
        if (_error is { } e) throw new IOException(e);
    }

    void Loop()
    {
        foreach (var data in _q.GetConsumingEnumerable())
        {
            try
            {
                if (_port is null)
                {
                    if (DateTime.UtcNow < _nextTry) continue;
                    _port = _opener(); _port.Open();
                }
                _port.Write(data, 0, data.Length);
                _error = null;
            }
            catch (Exception e)
            {
                _error = e.Message;
                _port?.Dispose(); _port = null;
                _nextTry = DateTime.UtcNow.AddSeconds(2);
            }
        }
        _port?.Dispose();
    }

    public void Dispose() { _q.CompleteAdding(); _t.Join(2000); }
}
