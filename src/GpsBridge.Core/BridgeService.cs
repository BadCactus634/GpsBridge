using System.Diagnostics;
using GpsBridge.Core.Gpx;
using GpsBridge.Core.Io;
using GpsBridge.Core.Nmea;
using GpsBridge.Core.Settings;
using GpsBridge.Core.Ubx;

namespace GpsBridge.Core;

public sealed class BridgeStats
{
    public long Good, Bad, UbxFrames;
    public double Rate;   // frasi valide al secondo (finestra di 1 s)
}

/// <summary>Collega sorgente -> splitter -> {Earth, GPX, stato} e gestisce la configurazione all'avvio.</summary>
public sealed class BridgeService : IDisposable
{
    readonly AppSettings _settings;
    readonly IRunnableSource _source;
    readonly bool _applyOnConnect;
    readonly IByteStream _earthOut;
    readonly Stopwatch _win = Stopwatch.StartNew();
    long _winCount;
    CancellationTokenSource? _cts;

    public FrameSplitter Splitter { get; } = new();
    public GnssState State { get; } = new();
    public GpxRecorder Gpx { get; }
    public EarthWriter Earth { get; }
    public UbxConfigurator Config { get; }
    public BridgeStats Stats { get; } = new();
    public Task Running { get; private set; } = Task.CompletedTask;
    public ConnState Connection { get; private set; } = ConnState.Disconnected;
    public string? FirmwareVersion { get; private set; }
    public ApplyResult? LastApply { get; private set; }
    public IRunnableSource Source => _source;

    public event Action<ConnState>? ConnectionChanged;
    public event Action? ConfigChanged;

    public BridgeService(AppSettings settings, IRunnableSource source, IByteStream earthOut, bool applyProfileOnConnect = true)
    {
        _settings = settings; _source = source; _earthOut = earthOut; _applyOnConnect = applyProfileOnConnect && settings.ApplyProfileOnConnect;
        Gpx = new GpxRecorder(settings.GpxDir, settings.MinMoveMeters);
        Earth = new EarthWriter(earthOut, settings.EarthHz);
        Config = new UbxConfigurator(b => _source.Write(b), Splitter);

        source.DataReceived += d => Splitter.Feed(d);
        Splitter.Nmea += OnNmea;
        Splitter.BadLine += _ => Stats.Bad++;
        Splitter.Ubx += _ => Stats.UbxFrames++;
        if (source is SerialSource ss) ss.StateChanged += SetConnection;
    }

    public static BridgeService CreateReal(AppSettings s)
    {
        var src = new SerialSource((n, b) => new SerialPortAdapter(n, b),
            () => SerialPortAdapter.List().Where(p => p != s.EarthPort && p != s.EarthPeerPort),
            s.RealPort, s.RealBaud, preferredPort: s.LastPort, preferredBaud: s.LastBaud);
        var earth = new PortWriter(() => new SerialPortAdapter(s.EarthPort, s.EarthBaud));
        return new BridgeService(s, src, earth);
    }

    void SetConnection(ConnState c)
    {
        Connection = c; ConnectionChanged?.Invoke(c);
        if (c == ConnState.Connected && _source is SerialSource sp && (_settings.LastPort != sp.PortName || _settings.LastBaud != sp.DetectedBaud))
        { _settings.LastPort = sp.PortName; _settings.LastBaud = sp.DetectedBaud; try { _settings.Save(AppSettings.DefaultPath); } catch { } }
        if (c == ConnState.Connected && _applyOnConnect) _ = ApplyProfileAsync();
    }

    public async Task ApplyProfileAsync()
    {
        try
        {
            FirmwareVersion = await Config.ReadVersionAsync();
            LastApply = await Config.ApplyAsync(ReceiverProfile.FromOptions(_settings.Receiver),
                UbxFrame.LayerRam | UbxFrame.LayerBbr | UbxFrame.LayerFlash);
        }
        catch (Exception e) { LastApply = new ApplyResult(false, false, Array.Empty<string>(), Array.Empty<string>(), e.Message); }
        ConfigChanged?.Invoke();
    }

    void OnNmea(NmeaSentence s)
    {
        Stats.Good++;
        if (++_winCount >= 1 && _win.Elapsed >= TimeSpan.FromSeconds(1))
        {
            Stats.Rate = _winCount / _win.Elapsed.TotalSeconds; _winCount = 0; _win.Restart();
        }
        State.Apply(s);
        Earth.OnSentence(s);
        if (s.Type == "RMC" && State.Position is { } pos)
            Gpx.OnFix(pos, State.Satellites.Count(x => x.Used), State.Hdop);
    }

    public void Start()
    {
        if (_cts is not null) return;
        RepairGpxFiles();
        _cts = new CancellationTokenSource();
        if (_source is not SerialSource) SetConnection(ConnState.Connected);
        Running = _source.RunAsync(_cts.Token);
        if (_settings.AutoStartRecording) Gpx.Start();
    }

    public void Stop()
    {
        _cts?.Cancel(); _cts = null;
        Gpx.Stop();
    }

    void RepairGpxFiles()
    {
        try
        {
            if (!Directory.Exists(_settings.GpxDir)) return;
            foreach (var f in Directory.EnumerateFiles(_settings.GpxDir, "*.gpx")) GpxRecorder.RepairIfOpen(f);
        }
        catch { /* la riparazione e' un di piu': mai bloccare l'avvio */ }
    }

    public void Dispose()
    {
        Stop();
        (_earthOut as IDisposable)?.Dispose();
    }
}
