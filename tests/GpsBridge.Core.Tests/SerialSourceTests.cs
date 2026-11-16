using System.Text;
using GpsBridge.Core.Io;
using Xunit;

namespace GpsBridge.Core.Tests;

sealed class FakePort : IPort
{
    readonly Func<byte[]?> _next;   // null = lancia IOException
    public FakePort(Func<byte[]?> next) => _next = next;
    public bool Disposed;
    public void Open() { }
    public int Read(byte[] buf, int off, int count)
    {
        Thread.Sleep(5);
        var d = _next();
        if (d is null) throw new IOException("scollegato");
        Array.Copy(d, 0, buf, off, Math.Min(d.Length, count));
        return Math.Min(d.Length, count);
    }
    public void Write(byte[] buf, int off, int count) { }
    public void Dispose() => Disposed = true;
}

public class SerialSourceTests
{
    static readonly byte[] Good = Encoding.ASCII.GetBytes(
        NmeaSentenceTests.Make("GPGGA,1,2,3") + "\r\n" + NmeaSentenceTests.Make("GNRMC,4,5,6") + "\r\n");
    static readonly byte[] Garbage = { 0xFF, 0x00, 0xFE, 0x80 };

    [Fact]
    public async Task Autobaud_finds_the_baud_that_gives_valid_sentences()
    {
        var tried = new List<int>();
        IPort Opener(string port, int baud) { tried.Add(baud); return new FakePort(() => baud == 38400 ? Good : Garbage); }
        var src = new SerialSource(Opener, () => new[] { "COM3" }, probe: TimeSpan.FromMilliseconds(200), backoffMs: new[] { 20 });
        var got = new TaskCompletionSource();
        src.DataReceived += _ => got.TrySetResult();
        using var cts = new CancellationTokenSource();
        var run = src.RunAsync(cts.Token);
        await Task.WhenAny(got.Task, Task.Delay(5000));
        Assert.True(got.Task.IsCompleted);
        Assert.Equal(38400, src.DetectedBaud);
        Assert.Equal(ConnState.Connected, src.State);
        cts.Cancel(); await run;
    }

    [Fact]
    public async Task Port_error_goes_disconnected_then_reconnects_without_throwing()
    {
        int opens = 0;
        IPort Opener(string port, int baud)
        {
            int n = ++opens; int reads = 0;
            // prima apertura: dati buoni per un po', poi l'USB viene staccato
            return new FakePort(() => n == 1 && ++reads > 60 ? null : Good);
        }
        var src = new SerialSource(Opener, () => new[] { "COM3" }, fixedBaud: 115200, probe: TimeSpan.FromMilliseconds(100), backoffMs: new[] { 20 });
        var states = new List<ConnState>();
        src.StateChanged += s => { lock (states) states.Add(s); };
        using var cts = new CancellationTokenSource();
        var run = src.RunAsync(cts.Token);
        var end = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < end)
        {
            lock (states) { if (states.Count(s => s == ConnState.Connected) >= 2) break; }
            await Task.Delay(20);
        }
        cts.Cancel(); await run;
        lock (states)
        {
            Assert.True(states.Count(s => s == ConnState.Connected) >= 2);
            Assert.Contains(ConnState.Disconnected, states);
        }
    }

    [Fact]
    public async Task Replay_stream_emits_the_file_lines()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "sample-multi-gnss.nmea");
        var rep = new ReplayStream(path, lineDelayMs: 1, loop: false);
        var sb = new StringBuilder();
        rep.DataReceived += d => sb.Append(Encoding.ASCII.GetString(d));
        await rep.RunAsync(CancellationToken.None);
        Assert.Contains("$GNGGA,123519.00", sb.ToString());
        Assert.Contains("GBGSV", sb.ToString());
    }
}
