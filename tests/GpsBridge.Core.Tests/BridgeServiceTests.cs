using System.Xml.Linq;
using GpsBridge.Core;
using GpsBridge.Core.Io;
using GpsBridge.Core.Nmea;
using GpsBridge.Core.Settings;
using Xunit;

namespace GpsBridge.Core.Tests;

public class BridgeServiceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "gpsb-br-" + Guid.NewGuid().ToString("N"));
    public BridgeServiceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Replay_file_flows_to_earth_gpx_and_state()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "sample-multi-gnss.nmea");
        var earth = new CaptureStream();
        var settings = new AppSettings { GpxDir = _dir, EarthHz = 1, KmlEnabled = false };
        var svc = new BridgeService(settings, new ReplayStream(path, lineDelayMs: 1, loop: false), earth, applyProfileOnConnect: false);
        GnssFix? lastFix = null;
        svc.State.Changed += () => lastFix = svc.State.Position ?? lastFix;

        svc.Gpx.Start();
        svc.Start();
        await svc.Running;
        svc.Gpx.Stop();

        Assert.NotNull(lastFix);
        Assert.Equal(48.1173, lastFix!.Lat, 4);
        Assert.Contains(earth.Lines, l => l.StartsWith("$GPGGA"));
        Assert.Contains(earth.Lines, l => l.StartsWith("$GPRMC"));
        Assert.DoesNotContain(earth.Lines, l => l.StartsWith("$GN"));
        Assert.True(svc.Stats.Good >= 13);
        var gpx = XDocument.Load(svc.Gpx.CurrentFile!);
        Assert.Single(gpx.Descendants().Where(e => e.Name.LocalName == "trkpt"));   // solo la prima epoca ha il fix
    }

    [Fact]
    public async Task Garbage_is_counted_not_forwarded()
    {
        var src = new PushSource();
        var earth = new CaptureStream();
        var svc = new BridgeService(new AppSettings { GpxDir = _dir, KmlEnabled = false }, src, earth, applyProfileOnConnect: false);
        svc.Start();
        src.Push("$GPGGA,1,2,3*00\r\n");             // checksum errato
        src.Push("$GPRMC,1,2,3,4\r\n");              // senza checksum
        await Task.Delay(50);
        Assert.Equal(0, svc.Stats.Good);
        Assert.True(svc.Stats.Bad >= 2);
        Assert.Empty(earth.Lines);
        svc.Stop();
    }
}

sealed class PushSource : IRunnableSource
{
    public event Action<byte[]>? DataReceived;
    public void Write(ReadOnlySpan<byte> data) { }
    public void Push(string s) => DataReceived?.Invoke(System.Text.Encoding.ASCII.GetBytes(s));
    public Task RunAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { });
}
