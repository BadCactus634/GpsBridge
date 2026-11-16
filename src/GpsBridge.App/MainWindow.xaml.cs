using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using GpsBridge.Core;
using GpsBridge.Core.Geo;
using GpsBridge.Core.Gpx;
using GpsBridge.Core.Io;
using GpsBridge.Core.Kml;
using GpsBridge.Core.Map;
using GpsBridge.Core.Nmea;
using GpsBridge.Core.Settings;
using GpsBridge.Core.Ubx;
using GpsBridge.Core.Update;
using GpsBridge.Core.Waypoints;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Path = System.IO.Path;

namespace GpsBridge.App;

public sealed record WpRow(Guid Id, Brush Brush, string Name, string Coord, string Ele, string Dist, string Bearing, string Notes);

public partial class MainWindow : Window
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly int[] Rates = { 1, 2, 5, 10 };
    static readonly int[] DynModels = { 0, 3, 4, 2, 6 };   // Portatile, Pedone, Auto, Fermo, Aereo
    static readonly Dictionary<string, Brush> SatColors = new()
    {
        ["GPS"] = Brushes.SteelBlue, ["Galileo"] = Brushes.DarkOrange, ["BeiDou"] = Brushes.Crimson,
        ["GLONASS"] = Brushes.SeaGreen, ["QZSS"] = Brushes.MediumPurple,
    };

    readonly AppSettings _s = AppSettings.Load(AppSettings.DefaultPath);
    readonly WaypointStore _wp = new(WaypointStore.DefaultPath);
    readonly HttpClient _http = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly string? _replay;
    BridgeService? _bridge;
    TileCache _tiles;
    GnssFix? _lastSent;
    Guid? _navId;
    bool _webReady, _loading = true, _showLoaded;
    IReadOnlyList<TrackPoint> _profilePts = Array.Empty<TrackPoint>();
    DateTime _lastSlow = DateTime.MinValue;
    string _trackKey = "";

    Brush B(string key) => (Brush)FindResource(key);

    public MainWindow()
    {
        InitializeComponent();
        SetIcons();
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--replay");
        if (i >= 0 && i + 1 < args.Length) _replay = args[i + 1];
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GpsBridge/1.0 (open source GPS tool)");
        _tiles = NewTileCache();
        foreach (var (_, name) in TileSources.Layers) CmbLayer.Items.Add(name);
        CmbLayer.SelectedIndex = Math.Max(0, TileSources.Layers.ToList().FindIndex(l => l.Id == _s.MapLayer));
        _wp.Changed += OnWaypointsChanged;
        _timer.Tick += (_, _) => Refresh();
        Loaded += async (_, _) =>
        {
            SettingsToUi();
            _loading = false;
            await InitWeb();
            StartBridge();
            _timer.Start();
            if (_s.CheckUpdates) { await Task.Delay(4000); await CheckForUpdateAsync(false); }
        };
        Closing += (_, _) => { _bridge?.Dispose(); };
    }

    // ======================= aggiornamenti (GitHub Releases) =======================
    // GPSBRIDGE_UPDATE_API serve solo per provare l'aggiornamento contro un server locale.
    readonly UpdateChecker _updater = new(new HttpClient { Timeout = TimeSpan.FromMinutes(10) }, "BadCactus634/GpsBridge",
        Environment.GetEnvironmentVariable("GPSBRIDGE_UPDATE_API") ?? "https://api.github.com");
    UpdateInfo? _update;

    /// <summary>Cerca una versione piu' nuova. manual = richiesto dall'utente (mostra sempre il risultato, ignora «salta»).</summary>
    async Task<string> CheckForUpdateAsync(bool manual)
    {
        var current = Version.Parse(AboutDialog.Version);
        var u = await _updater.CheckAsync(current);
        if (u is null) return $"Hai gia' l'ultima versione ({AboutDialog.Version}), oppure GitHub non e' raggiungibile.";
        if (!manual && _s.SkipVersion == u.Version.ToString(3)) return "";
        _update = u;
        TxtUpdate.Text = $"È disponibile GpsBridge {u.Version.ToString(3)} (hai la {AboutDialog.Version})";
        PbUpd.Visibility = Visibility.Collapsed; BtnUpdNow.IsEnabled = true;
        UpdateBanner.Visibility = Visibility.Visible;
        return $"Nuova versione {u.Version.ToString(3)} disponibile: usa «Scarica e installa» nella barra in alto.";
    }

    void BtnAbout_Click(object s, RoutedEventArgs e) =>
        new AboutDialog { Owner = this, CheckForUpdates = () => CheckForUpdateAsync(true) }.ShowDialog();

    void BtnUpdLater_Click(object s, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    void BtnUpdSkip_Click(object s, RoutedEventArgs e)
    {
        if (_update is not null) { _s.SkipVersion = _update.Version.ToString(3); try { _s.Save(AppSettings.DefaultPath); } catch { } }
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    void BtnUpdNotes_Click(object s, RoutedEventArgs e)
    {
        if (_update is null) return;
        var notes = string.IsNullOrWhiteSpace(_update.Notes) ? "(nessuna nota)" : _update.Notes.Trim();
        if (MessageBox.Show(this, notes + "\n\nAprire la pagina della versione su GitHub?", $"GpsBridge {_update.Version.ToString(3)}",
                MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes && _update.PageUrl.StartsWith("https://github.com/"))
            OpenShell(_update.PageUrl, "il browser");
    }

    async void BtnUpdNow_Click(object s, RoutedEventArgs e)
    {
        if (_update is not { } u) return;
        BtnUpdNow.IsEnabled = false; PbUpd.Value = 0; PbUpd.Visibility = Visibility.Visible;
        TxtUpdate.Text = $"Scarico GpsBridge {u.Version.ToString(3)}...";
        var path = Path.Combine(Path.GetTempPath(), $"GpsBridge-setup-{u.Version.ToString(3)}.exe");
        try
        {
            await _updater.DownloadAsync(u, path, new Progress<double>(p => PbUpd.Value = p));
        }
        catch (Exception ex)
        {
            App.Log("Download dell'aggiornamento", ex);
            TxtUpdate.Text = "Download non riuscito: " + ex.Message;
            PbUpd.Visibility = Visibility.Collapsed; BtnUpdNow.IsEnabled = true;
            return;
        }
        string rec = _bridge?.Gpx.IsRecording == true ? "\nLa registrazione GPX in corso verrà salvata." : "";
        if (MessageBox.Show(this, $"Scaricata la versione {u.Version.ToString(3)}. Installarla ora?\nL'app si chiude e si riapre da sola dopo l'installazione.{rec}",
                "GpsBridge", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            TxtUpdate.Text = $"Versione {u.Version.ToString(3)} scaricata: premi «Scarica e installa» quando vuoi."; PbUpd.Visibility = Visibility.Collapsed; BtnUpdNow.IsEnabled = true; return;
        }
        try
        {
            // L'installer chiede il permesso di amministratore (UAC), sostituisce i file e rilancia l'app (/RELAUNCH=1).
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART /RELAUNCH=1") { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
        {
            TxtUpdate.Text = "Installazione annullata (permesso di amministratore negato)."; PbUpd.Visibility = Visibility.Collapsed; BtnUpdNow.IsEnabled = true;
        }
        catch (Exception ex)
        {
            App.Log("Avvio dell'installer", ex);
            TxtUpdate.Text = "Non riesco ad avviare l'installer: " + ex.Message; PbUpd.Visibility = Visibility.Collapsed; BtnUpdNow.IsEnabled = true;
        }
    }

    // ======================= icone dei pulsanti (vettoriali, nitide a ogni scala) =======================
    // Disegni su una griglia 24x24; stroke=true: solo contorno.
    static readonly Dictionary<string, (string Data, bool Stroke)> Icons = new()
    {
        ["info"] = ("F0 M12,2 A10,10 0 1 1 11.99,2 Z M10.9,10.4 L13.1,10.4 L13.1,17.4 L10.9,17.4 Z M10.9,6.6 L13.1,6.6 L13.1,8.8 L10.9,8.8 Z", false),
        ["follow"] = ("M12,6 A6,6 0 1 1 11.99,6 Z M12,1.5 V6.5 M12,17.5 V22.5 M1.5,12 H6.5 M17.5,12 H22.5", true),
        ["rec"] = ("M12,4 A8,8 0 1 1 11.99,4 Z", false),
        ["stop"] = ("M6,6 H18 V18 H6 Z", false),
        ["pause"] = ("M6.5,5 H10.5 V19 H6.5 Z M13.5,5 H17.5 V19 H13.5 Z", false),
        ["play"] = ("M7.5,4.5 L19.5,12 L7.5,19.5 Z", false),
        ["plus"] = ("M10.8,4.5 H13.2 V10.8 H19.5 V13.2 H13.2 V19.5 H10.8 V13.2 H4.5 V10.8 H10.8 Z", false),
    };

    /// <summary>Contenuto di un pulsante: icona vettoriale + testo, entrambi del colore del pulsante.</summary>
    static UIElement IconText(string icon, string text)
    {
        var (data, stroke) = Icons[icon];
        var fg = new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) };
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(data), Width = 13, Height = 13, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0), SnapsToDevicePixels = false };
        if (stroke) { path.SetBinding(Shape.StrokeProperty, fg); path.StrokeThickness = 1.7; path.StrokeLineJoin = PenLineJoin.Round; path.StrokeStartLineCap = PenLineCap.Round; path.StrokeEndLineCap = PenLineCap.Round; }
        else path.SetBinding(Shape.FillProperty, fg);
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label.SetBinding(TextBlock.ForegroundProperty, fg);
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(path); sp.Children.Add(label);
        return sp;
    }

    string _recState = "";
    void SetRecButtons(string state)
    {
        if (state == _recState) return;
        _recState = state;
        string text = state switch { "rec" => "REC", "play" => "RIPRENDI", _ => "PAUSA" };
        BtnRecMain.Content = IconText(state, text); BtnRecMain2.Content = IconText(state, text);
    }

    void SetIcons()
    {
        BtnAbout.Content = IconText("info", "Info");
        BtnFollow.Content = IconText("follow", "Segui posizione");
        BtnMarkMap.Content = IconText("plus", "Segna punto qui");
        BtnMarkTab.Content = IconText("plus", "Segna qui");
        BtnRecStop.Content = IconText("stop", "STOP");
        BtnRecStop2.Content = IconText("stop", "STOP e salva");
        SetRecButtons("rec");
        // le icone non hanno testo leggibile dai lettori di schermo: il nome va dato a parte
        foreach (var (b, n) in new[] { (BtnAbout, "Informazioni"), (BtnFollow, "Segui posizione"), (BtnMarkMap, "Segna punto qui"),
                                       (BtnMarkTab, "Segna punto qui"), (BtnRecStop, "Stop registrazione"), (BtnRecStop2, "Stop registrazione e salva") })
            System.Windows.Automation.AutomationProperties.SetName(b, n);
    }

    TileCache NewTileCache() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpsBridge", "tiles"),
        _s.TileCacheMb * 1_000_000, url => _http.GetByteArrayAsync(url));

    // ======================= mappa =======================
    async Task InitWeb()
    {
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpsBridge", "webview");
            var env = await CoreWebView2Environment.CreateAsync(null, data);
            await Web.EnsureCoreWebView2Async(env);
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping("app.local",
                Path.Combine(AppContext.BaseDirectory, "Assets", "map"), CoreWebView2HostResourceAccessKind.Allow);
            Web.CoreWebView2.AddWebResourceRequestedFilter("https://tiles.local/*", CoreWebView2WebResourceContext.All);
            Web.CoreWebView2.WebResourceRequested += OnTileRequested;
            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            Web.Source = new Uri("https://app.local/map.html");
        }
        catch (Exception e)
        {
            TxtMapMsg.Text = "Mappa non disponibile (manca WebView2?): " + e.Message;
            TxtMapMsg.Visibility = Visibility.Visible;
        }
    }

    async void OnTileRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var def = e.GetDeferral();
        try
        {
            var parts = new Uri(e.Request.Uri).AbsolutePath.Trim('/').Replace(".png", "").Split('/');
            byte[]? data = null;
            if (parts.Length == 4 && int.TryParse(parts[1], out var z) && int.TryParse(parts[2], out var x) && int.TryParse(parts[3], out var y))
                data = await _tiles.GetAsync(parts[0], z, x, y);
            e.Response = data is not null
                ? Web.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(data), 200, "OK", "Content-Type: image/png")
                : Web.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
        }
        finally { def.Complete(); }
    }

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var r = doc.RootElement;
            switch (r.GetProperty("type").GetString())
            {
                case "ready": _webReady = true; _lastSent = null; PushMapState(); break;   // _lastSent = null: al prossimo giro rimanda la posizione, anche se non e' cambiata
                case "addAt": OpenDialog(null, r.GetProperty("lat").GetDouble(), r.GetProperty("lon").GetDouble(), null); break;
                case "wp":
                    if (_wp.Items.FirstOrDefault(w => w.Id == Guid.Parse(r.GetProperty("id").GetString()!)) is { } w)
                        OpenDialog(w, w.Lat, w.Lon, w.Ele);
                    break;
            }
        }
        catch { /* messaggio non valido: ignorato */ }
    }

    void PostMap(object msg) { try { if (_webReady) Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(msg)); } catch { } }

    void PushMapState()
    {
        PostMap(new { type = "theme", dark = App.IsDark });
        PostMap(new { type = "layer", name = TileSources.Layers[Math.Max(0, CmbLayer.SelectedIndex)].Id });
        PostWaypointsToMap();
        if (_navId is { } id && _wp.Items.FirstOrDefault(w => w.Id == id) is { } t)
            PostMap(new { type = "goto", lat = t.Lat, lon = t.Lon, off = false });
    }

    void PostWaypointsToMap() =>
        PostMap(new { type = "wpts", items = _wp.Items.Select(w => new { id = w.Id.ToString(), name = w.Name, color = w.Color, lat = w.Lat, lon = w.Lon }) });

    void CmbLayer_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || CmbLayer.SelectedIndex < 0) return;
        var id = TileSources.Layers[CmbLayer.SelectedIndex].Id;
        _s.MapLayer = id; try { _s.Save(AppSettings.DefaultPath); } catch { }
        PostMap(new { type = "layer", name = id });
    }

    void BtnFollow_Click(object s, RoutedEventArgs e) => PostMap(new { type = "follow" });
    void BtnClearTrack_Click(object s, RoutedEventArgs e)
    {
        PostMap(new { type = "clear" }); _bridge?.Live?.ClearTrack();
        _showLoaded = false; TrName.Text = "Nessuna traccia mostrata"; TrStats.Text = ""; Profile.Children.Clear(); _profilePts = Array.Empty<TrackPoint>();
        PostMap(new { type = "view", tracks = Array.Empty<object>() });
    }

    // ======================= ponte (ricevitore) =======================
    void StartBridge()
    {
        if (_bridge is not null) return;
        _bridge = _replay is not null
            ? new BridgeService(_s, new ReplayStream(_replay), _wp, applyProfileOnConnect: false)
            : BridgeService.CreateReal(_s, _wp);
        _bridge.Start();
        BtnEarth.IsEnabled = _bridge.Live is not null;
    }

    void RestartBridge() { _bridge?.Dispose(); _bridge = null; _lastSent = null; StartBridge(); }

    // ======================= registrazione =======================
    void BtnRecMain_Click(object s, RoutedEventArgs e)
    {
        var g = _bridge?.Gpx; if (g is null) return;
        if (!g.IsRecording) { g.Start(); _showLoaded = false; }
        else if (g.IsPaused) g.Resume();
        else g.Pause();
    }

    void BtnRecStop_Click(object s, RoutedEventArgs e)
    {
        var g = _bridge?.Gpx; if (g is null || !g.IsRecording) return;
        string? file = g.CurrentFile; int n = g.Points; g.Stop();
        TrStatus.Text = $"Traccia salvata: {Path.GetFileName(file)} ({n} punti)";
        _trackKey = ""; if (file is not null) ShowTrack(file);
    }

    void BtnLoad_Click(object s, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_s.GpxDir);
        var dlg = new OpenFileDialog { Filter = "Tracce GPX (*.gpx)|*.gpx|Tutti i file|*.*", InitialDirectory = _s.GpxDir, Title = "Carica una traccia GPX" };
        if (dlg.ShowDialog(this) == true) ShowTrack(dlg.FileName);
    }

    void BtnShowTrack_Click(object s, RoutedEventArgs e)
    {
        if (TrList.SelectedItem is string name) ShowTrack(Path.Combine(_s.GpxDir, name));
    }

    void TrList_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e) => BtnShowTrack_Click(s, e);

    void BtnOpenDir_Click(object s, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_s.GpxDir);
        System.Diagnostics.Process.Start("explorer.exe", _s.GpxDir);
    }

    void ShowTrack(string path)
    {
        try
        {
            var tracks = GpxReader.ReadTracks(path);
            var all = tracks.SelectMany(t => t.Points).ToList();
            if (all.Count == 0) { MessageBox.Show(this, "Il file non contiene punti di traccia.", "GpsBridge"); return; }
            _showLoaded = true;
            int step = Math.Max(1, all.Count / 4000);
            PostMap(new { type = "view", tracks = tracks.Select(t => new { name = t.Name, pts = t.Points.Where((_, i) => i % step == 0).Select(p => new[] { p.Lat, p.Lon }) }) });
            TrName.Text = Path.GetFileName(path);
            ShowStats(all);
        }
        catch (Exception e) { MessageBox.Show(this, "Non riesco a leggere la traccia: " + e.Message, "GpsBridge"); }
    }

    void ShowStats(IReadOnlyList<TrackPoint> pts)
    {
        var st = GpxReader.Stats(pts); var u = _s.Units;
        string T(TimeSpan? t) => t is null ? "—" : $"{(int)t.Value.TotalHours}:{t.Value.Minutes:00}:{t.Value.Seconds:00}";
        TrStats.Text =
            $"DISTANZA      {GeoMath.Distance(st.DistanceM, u)}\n" +
            $"DURATA        {T(st.Duration)}\n" +
            $"IN MOVIMENTO  {T(st.MovingTime)}\n" +
            $"VEL. MEDIA    {GeoMath.Speed(st.AvgSpeedKnots, u)}\n" +
            $"VEL. MAX      {GeoMath.Speed(st.MaxSpeedKnots, u)}\n" +
            $"SALITA        {(st.AscentM is null ? "—" : "+" + GeoMath.Elevation(st.AscentM, u))}\n" +
            $"DISCESA       {(st.DescentM is null ? "—" : "-" + GeoMath.Elevation(st.DescentM, u))}\n" +
            $"QUOTA         min {GeoMath.Elevation(st.MinEle, u)} / max {GeoMath.Elevation(st.MaxEle, u)}\n" +
            $"PUNTI         {st.Points}";
        _profilePts = pts; DrawProfile();
    }

    void Profile_SizeChanged(object s, SizeChangedEventArgs e) => DrawProfile();

    void DrawProfile()
    {
        Profile.Children.Clear();
        double w = Profile.ActualWidth, h = Profile.ActualHeight;
        var pts = _profilePts;
        if (w < 50 || h < 50) return;
        var with = pts.Where(p => p.Ele is not null).ToList();
        if (with.Count < 2)
        {
            Profile.Children.Add(new TextBlock { Text = "Nessun dato di quota in questa traccia", Foreground = B("Dim"), Margin = new Thickness(12) });
            return;
        }
        var dist = new List<double> { 0 };
        for (int i = 1; i < with.Count; i++) dist.Add(dist[^1] + GeoMath.DistanceMeters(with[i - 1].Lat, with[i - 1].Lon, with[i].Lat, with[i].Lon));
        double min = with.Min(p => p.Ele!.Value), max = with.Max(p => p.Ele!.Value), total = Math.Max(1, dist[^1]);
        if (max - min < 10) { max += 5; min -= 5; }
        const double L = 52, Rm = 10, T = 14, Bm = 22;
        var fill = new Polygon { Fill = new SolidColorBrush(Color.FromArgb(60, ((SolidColorBrush)B("Accent")).Color.R, ((SolidColorBrush)B("Accent")).Color.G, ((SolidColorBrush)B("Accent")).Color.B)) };
        var line = new Polyline { Stroke = B("Accent"), StrokeThickness = 1.6 };
        fill.Points.Add(new Point(L, h - Bm));
        for (int i = 0; i < with.Count; i++)
        {
            double x = L + dist[i] / total * (w - L - Rm), y = T + (1 - (with[i].Ele!.Value - min) / (max - min)) * (h - T - Bm);
            line.Points.Add(new Point(x, y)); fill.Points.Add(new Point(x, y));
        }
        fill.Points.Add(new Point(w - Rm, h - Bm));
        for (int k = 0; k <= 3; k++)
        {
            double y = T + k / 3.0 * (h - T - Bm);
            Profile.Children.Add(new Line { X1 = L, X2 = w - Rm, Y1 = y, Y2 = y, Stroke = B("Line"), StrokeThickness = 1 });
            var lbl = new TextBlock { Text = GeoMath.Elevation(max - k / 3.0 * (max - min), _s.Units), Foreground = B("Dim"), FontSize = 10, FontFamily = (FontFamily)FindResource("Mono") };
            Canvas.SetLeft(lbl, 4); Canvas.SetTop(lbl, y - 7); Profile.Children.Add(lbl);
        }
        var dl = new TextBlock { Text = GeoMath.Distance(total, _s.Units), Foreground = B("Dim"), FontSize = 10 };
        Canvas.SetLeft(dl, w - Rm - 50); Canvas.SetTop(dl, h - Bm + 4);
        Profile.Children.Add(fill); Profile.Children.Add(line); Profile.Children.Add(dl);
    }

    // ======================= Google Earth =======================
    void BtnEarth_Click(object s, RoutedEventArgs e) => OpenShell(BridgeService.LinkFile, "Google Earth");

    static void OpenShell(string file, string what)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Non riesco ad aprire {what}: {ex.Message}\nApri a mano il file {file}"); }
    }

    void BtnWpEarth_Click(object s, RoutedEventArgs e)
    {
        var f = Path.Combine(Path.GetDirectoryName(BridgeService.LinkFile)!, "punti.kml");
        File.WriteAllText(f, KmlBuilder.Position(null, Array.Empty<(double, double)>(), "", false, _wp.Items), new UTF8Encoding(false));
        OpenShell(f, "Google Earth");
    }

    // ======================= punti =======================
    void OnWaypointsChanged()
    {
        Dispatcher.Invoke(() => { PostWaypointsToMap(); RefreshWpGrid(); });
    }

    void BtnMark_Click(object s, RoutedEventArgs e)
    {
        var p = _bridge?.State.Position;
        if (p is null) { MessageBox.Show(this, "Nessun fix GPS: attendi l'aggancio dei satelliti oppure usa «Nuovo da coordinate».", "GpsBridge"); return; }
        OpenDialog(null, p.Lat, p.Lon, p.Alt);
    }

    void BtnNewWp_Click(object s, RoutedEventArgs e) => OpenDialog(null, double.NaN, double.NaN, null);

    void OpenDialog(Waypoint? w, double lat, double lon, double? ele)
    {
        var dlg = new WaypointDialog(w, lat, lon, ele, _s.CoordFormat) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        if (dlg.Deleted && w is not null) { _wp.Remove(w.Id); if (_navId == w.Id) NavOff(); return; }
        if (dlg.GoTo && w is not null) { SetNav(w.Id); return; }
        if (dlg.Result is null) return;
        if (w is null) _wp.Add(dlg.Result); else _wp.Update(dlg.Result);
    }

    WpRow? SelWp() { if (GridWp.SelectedItem is not WpRow r) { MessageBox.Show(this, "Seleziona un punto dall'elenco.", "GpsBridge"); return null; } return r; }
    Waypoint? Find(Guid id) => _wp.Items.FirstOrDefault(w => w.Id == id);

    void BtnEditWp_Click(object s, RoutedEventArgs e) { if (SelWp() is { } r && Find(r.Id) is { } w) OpenDialog(w, w.Lat, w.Lon, w.Ele); }
    void GridWp_DoubleClick(object s, System.Windows.Input.MouseButtonEventArgs e) { if (GridWp.SelectedItem is WpRow r && Find(r.Id) is { } w) OpenDialog(w, w.Lat, w.Lon, w.Ele); }
    void BtnGoWp_Click(object s, RoutedEventArgs e) { if (SelWp() is { } r) { SetNav(r.Id); Tabs.SelectedIndex = 0; } }
    void BtnShowWp_Click(object s, RoutedEventArgs e)
    {
        if (SelWp() is { } r && Find(r.Id) is { } w) { PostMap(new { type = "center", lat = w.Lat, lon = w.Lon }); Tabs.SelectedIndex = 0; }
    }
    void BtnCopyWp_Click(object s, RoutedEventArgs e)
    {
        if (SelWp() is { } r && Find(r.Id) is { } w) Clipboard.SetText(GeoMath.FormatCoord(w.Lat, w.Lon, _s.CoordFormat));
    }
    void BtnDelWp_Click(object s, RoutedEventArgs e)
    {
        if (SelWp() is not { } r) return;
        if (MessageBox.Show(this, $"Eliminare il punto «{r.Name}»?", "GpsBridge", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _wp.Remove(r.Id); if (_navId == r.Id) NavOff();
    }

    void BtnImportWp_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "GPX (*.gpx)|*.gpx|Tutti i file|*.*", Title = "Importa punti da un GPX" };
        if (dlg.ShowDialog(this) != true) return;
        try { int n = _wp.ImportGpx(dlg.FileName); MessageBox.Show(this, n == 0 ? "Nessun punto nuovo nel file." : $"Importati {n} punti.", "GpsBridge"); }
        catch (Exception ex) { MessageBox.Show(this, "Importazione non riuscita: " + ex.Message, "GpsBridge"); }
    }

    void BtnExportWp_Click(object s, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "GPX (*.gpx)|*.gpx", FileName = "punti.gpx", Title = "Esporta i punti in GPX" };
        if (dlg.ShowDialog(this) != true) return;
        try { _wp.ExportGpx(dlg.FileName); MessageBox.Show(this, $"Esportati {_wp.Items.Count} punti.", "GpsBridge"); }
        catch (Exception ex) { MessageBox.Show(this, "Esportazione non riuscita: " + ex.Message, "GpsBridge"); }
    }

    void RefreshWpGrid()
    {
        var pos = _bridge?.State.Position; var sel = (GridWp.SelectedItem as WpRow)?.Id;
        var rows = _wp.Items.Select(w =>
        {
            string d = "—", b = "—";
            if (pos is not null)
            {
                d = GeoMath.Distance(GeoMath.DistanceMeters(pos.Lat, pos.Lon, w.Lat, w.Lon), _s.Units);
                double br = GeoMath.BearingDeg(pos.Lat, pos.Lon, w.Lat, w.Lon); b = $"{br:0}° {GeoMath.Cardinal(br)}";
            }
            Brush br2 = new SolidColorBrush((Color)ColorConverter.ConvertFromString(w.Color));
            return new WpRow(w.Id, br2, w.Name, GeoMath.FormatCoord(w.Lat, w.Lon, _s.CoordFormat), GeoMath.Elevation(w.Ele, _s.Units), d, b, w.Notes);
        }).ToList();
        GridWp.ItemsSource = rows;
        if (sel is { } id) GridWp.SelectedItem = rows.FirstOrDefault(r => r.Id == id);
    }

    // ======================= navigazione =======================
    void SetNav(Guid id)
    {
        if (Find(id) is not { } w) return;
        _navId = id; BtnNavOff.IsEnabled = true;
        PostMap(new { type = "goto", lat = w.Lat, lon = w.Lon, off = false });
    }

    void BtnNavOff_Click(object s, RoutedEventArgs e) => NavOff();

    void NavOff()
    {
        _navId = null; BtnNavOff.IsEnabled = false;
        NavName.Text = "Nessuna destinazione"; NavDist.Text = NavBrg.Text = NavRel.Text = NavEta.Text = "—";
        PostMap(new { type = "goto", off = true });
    }

    void BtnCopy_Click(object s, RoutedEventArgs e)
    {
        if (_bridge?.State.Position is { } p) Clipboard.SetText(GeoMath.FormatCoord(p.Lat, p.Lon, _s.CoordFormat));
    }

    // ======================= aggiornamento periodico =======================
    void Tabs_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        _lastSlow = DateTime.MinValue; Refresh();
    }

    void Refresh()
    {
        if (_bridge is null) return;
        var st = _bridge.State; var pos = st.Position; var u = _s.Units;
        int used = st.Satellites.Count(x => x.Used), seen = st.Satellites.Count;

        // testata
        bool conn = _bridge.Source is not SerialSource ss || ss.State == ConnState.Connected;
        LedConn.Fill = !conn ? B("Bad") : pos is null ? B("Amber") : B("Good");
        TxtConn.Text = _bridge.Source is SerialSource sp
            ? (sp.State == ConnState.Connected ? $"Ricevitore collegato · {sp.PortName} · {sp.DetectedBaud} baud" : "Ricerca del ricevitore...")
            : $"Prova: riproduzione di {Path.GetFileName(_replay)}";
        var ap = _bridge.LastApply;
        TxtSub.Text = (_bridge.FirmwareVersion is { } v ? v.Split('|')[0].Trim() : "") +
                      (ap is { Ok: true } ? " · " + string.Join(" ", ap.Active) : ap is { Ok: false } ? " · config non riuscita" : "");
        var g = _bridge.Gpx;
        SetRecButtons(!g.IsRecording ? "rec" : g.IsPaused ? "play" : "pause");
        BtnRecStop.IsEnabled = BtnRecStop2.IsEnabled = g.IsRecording;
        LedRec.Fill = g.IsRecording ? (g.IsPaused ? B("Amber") : B("Bad")) : B("Thumb");
        TxtRec.Text = g.IsRecording ? $"{(g.IsPaused ? "PAUSA" : "REC")} · {g.Points} punti" : "REC fermo";
        if (g.IsRecording) TrStatus.Text = $"{(g.IsPaused ? "In pausa" : "Registrazione in corso")}: {Path.GetFileName(g.CurrentFile)} ({g.Points} punti)";
        else if (!TrStatus.Text.StartsWith("Traccia salvata")) TrStatus.Text = "Registrazione ferma";

        TxtCaps.Text = ap?.Caps is { Simultaneous: > 0 } cp
            ? $"Questo ricevitore usa al massimo {cp.Simultaneous} costellazioni insieme (oltre tornerebbe al solo GPS). Se ne spunti di piu' tra GPS, Galileo, GLONASS e BeiDou, l'app ne attiva {cp.Simultaneous} in questo ordine di preferenza; QZSS e SBAS in piu'."
            : "";

        // barra di stato
        SbFix.Text = $"{DiagnosticsModel.FixText(st.FixQuality)}  ·  sat {used}/{seen}  ·  {_bridge.Stats.Rate:0.0} frasi/s  ·  errori {_bridge.Stats.Bad}";
        SbKml.Text = _bridge.Live is not null ? $"KML live 127.0.0.1:{_bridge.Live.Port}" : _bridge.KmlError ?? "KML spento";

        // strumenti
        InPos.Text = pos is null ? "—" : GeoMath.FormatCoord(pos.Lat, pos.Lon, _s.CoordFormat);
        InAlt.Text = GeoMath.Elevation(pos?.Alt, u);
        InSpd.Text = GeoMath.Speed(pos?.SpeedKnots, u);
        InHdg.Text = pos?.CourseDeg is { } c ? $"{c:0}° {GeoMath.Cardinal(c)}" : "—";
        InFix.Text = pos is null ? "NO FIX" : DiagnosticsModel.FixText(st.FixQuality);
        InDop.Text = st.Hdop?.ToString("0.0", Inv) ?? "—";
        InSat.Text = $"{used}/{seen}";
        InUtc.Text = pos?.Utc.ToString("HH:mm:ss") ?? "—";

        if (pos is not null && !pos.Equals(_lastSent))
        {
            _lastSent = pos;
            PostMap(new { type = "pos", lat = pos.Lat, lon = pos.Lon, track = true });
        }

        // navigazione
        if (_navId is { } nid)
        {
            if (Find(nid) is not { } t) NavOff();
            else
            {
                NavName.Text = "→ " + t.Name;
                if (pos is not null)
                {
                    var n = Navigation.To(pos.Lat, pos.Lon, pos.CourseDeg, pos.SpeedKnots, t.Lat, t.Lon);
                    NavDist.Text = n.DistanceM < 10 ? "ARRIVATO" : GeoMath.Distance(n.DistanceM, u);
                    NavBrg.Text = $"{n.BearingDeg:0}° {n.Cardinal}";
                    NavRel.Text = n.RelativeDeg is { } r ? $"{(r >= 0 ? "→" : "←")} {Math.Abs(r):0}°" : "—";
                    NavEta.Text = n.Eta is { } eta ? (eta.TotalHours >= 1 ? $"{(int)eta.TotalHours}h {eta.Minutes:00}m" : $"{eta.Minutes}m {eta.Seconds:00}s") : "—";
                }
            }
        }

        // parti lente (ogni 2 s) in base alla scheda
        if (DateTime.UtcNow - _lastSlow < TimeSpan.FromSeconds(2)) return;
        _lastSlow = DateTime.UtcNow;
        switch (Tabs.SelectedIndex)
        {
            case 1: RefreshWpGrid(); break;
            case 2: RefreshTracksTab(g); break;
            case 3: RefreshDiagnostics(); break;
        }
    }

    void RefreshTracksTab(GpxRecorder g)
    {
        try
        {
            var files = Directory.Exists(_s.GpxDir)
                ? Directory.EnumerateFiles(_s.GpxDir, "*.gpx").Select(Path.GetFileName).OrderByDescending(x => x).ToList()
                : new List<string?>();
            var key = string.Join("|", files);
            if (key != _trackKey) { _trackKey = key; var sel = TrList.SelectedItem; TrList.ItemsSource = files; TrList.SelectedItem = sel; }
        }
        catch { }
        if (g.IsRecording && !_showLoaded)
        {
            TrName.Text = "Registrazione in corso";
            var snap = g.Snapshot();
            if (snap.Count > 1) ShowStats(snap);
        }
    }

    void RefreshDiagnostics()
    {
        var st = _bridge!.State; var pos = st.Position; var u = _s.Units;
        DgFix.Text = "Fix: " + DiagnosticsModel.FixText(st.FixQuality);
        DgDop.Text = $"HDOP {st.Hdop:0.0}  PDOP {st.Pdop:0.0}  VDOP {st.Vdop:0.0}";
        DgPos.Text = pos is null ? "Posizione: —" : "Posizione: " + GeoMath.FormatCoord(pos.Lat, pos.Lon, _s.CoordFormat);
        DgSpeed.Text = "Velocita': " + GeoMath.Speed(pos?.SpeedKnots, u) + (pos?.CourseDeg is { } c ? $"  rotta {c:0}°" : "");
        DgRate.Text = $"Frasi valide: {_bridge.Stats.Rate:0.0}/s  (totale {_bridge.Stats.Good})";
        DgErr.Text = $"Righe scartate: {_bridge.Stats.Bad}  riconnessioni: {(_bridge.Source as SerialSource)?.Reconnects ?? 0}";
        var ap = _bridge.LastApply;
        DgVer.Text = (_bridge.FirmwareVersion is { } v ? "Ricevitore: " + v : "Ricevitore: versione non letta") +
                     (ap is null ? "" : "\nCostellazioni attive: " + (ap.Ok ? string.Join(", ", ap.Active) +
                        (ap.Dropped.Count > 0 ? "  (non attivate: " + string.Join(", ", ap.Dropped) + (ap.Caps is { Simultaneous: > 0 } ? $"; il chip ne usa al massimo {ap.Caps.Simultaneous} insieme" : "") + ")" : "") : "configurazione non riuscita: " + ap.Error));
        GridConst.ItemsSource = DiagnosticsModel.Rows(st);
        GridSats.ItemsSource = st.Satellites;
        DrawSky(DiagnosticsModel.Sky(st));
    }

    void DrawSky(IReadOnlyList<SkyPoint> pts)
    {
        Sky.Children.Clear();
        const double c = 180, r = 160;
        var grid = B("Line");
        foreach (var f in new[] { 1.0, 0.667, 0.333 })
            Sky.Children.Add(new Ellipse { Width = 2 * r * f, Height = 2 * r * f, Stroke = grid, StrokeThickness = 1, Margin = new Thickness(c - r * f, c - r * f, 0, 0) });
        Sky.Children.Add(new Line { X1 = c - r, X2 = c + r, Y1 = c, Y2 = c, Stroke = grid });
        Sky.Children.Add(new Line { X1 = c, X2 = c, Y1 = c - r, Y2 = c + r, Stroke = grid });
        Sky.Children.Add(new TextBlock { Text = "N", Margin = new Thickness(c - 4, 0, 0, 0), FontWeight = FontWeights.Bold, Foreground = B("Amber") });
        foreach (var p in pts)
        {
            var brush = SatColors.TryGetValue(p.Constellation, out var b) ? b : Brushes.Gray;
            double x = c + p.X * r, y = c - p.Y * r;
            Sky.Children.Add(new Ellipse { Width = 14, Height = 14, Fill = p.Used ? brush : Brushes.Transparent, Stroke = brush, StrokeThickness = 2, Margin = new Thickness(x - 7, y - 7, 0, 0) });
            Sky.Children.Add(new TextBlock { Text = p.Prn.ToString(), FontSize = 9, Margin = new Thickness(x + 8, y - 6, 0, 0) });
        }
    }

    // ======================= impostazioni =======================
    void SettingsToUi()
    {
        CmbRealPort.ItemsSource = SerialPortAdapter.List().ToList();
        CmbRealPort.Text = _s.RealPort ?? "";
        CmbRealBaud.SelectedIndex = _s.RealBaud is { } rb ? Math.Max(0, new[] { 0, 4800, 9600, 38400, 115200, 230400 }.ToList().IndexOf(rb)) : 0;
        CmbTheme.SelectedIndex = _s.Theme switch { "light" => 1, "auto" => 2, _ => 0 };
        CmbUnits.SelectedIndex = (int)_s.Units; CmbCoord.SelectedIndex = (int)_s.CoordFormat;
        TxtGpxDir.Text = _s.GpxDir; TxtMinMove.Text = _s.MinMoveMeters.ToString(Inv); TxtTileMb.Text = _s.TileCacheMb.ToString(Inv);
        ChkKml.IsChecked = _s.KmlEnabled; ChkKmlFollow.IsChecked = _s.KmlFollow; TxtKmlPort.Text = _s.KmlPort.ToString(Inv);
        ChkAutoRec.IsChecked = _s.AutoStartRecording; ChkWinStart.IsChecked = _s.StartWithWindows; ChkUpdates.IsChecked = _s.CheckUpdates; ChkApplyOnConnect.IsChecked = _s.ApplyProfileOnConnect;
        OptionsToUi(_s.Receiver);
    }

    void OptionsToUi(ReceiverOptions o)
    {
        ChkGps.IsChecked = o.Gps; ChkGal.IsChecked = o.Galileo; ChkBds.IsChecked = o.BeiDou;
        ChkGlo.IsChecked = o.Glonass; ChkQzss.IsChecked = o.Qzss; ChkSbas.IsChecked = o.Sbas;
        CmbRate.SelectedIndex = Math.Max(0, Array.IndexOf(Rates, o.RateHz));
        CmbDyn.SelectedIndex = Math.Max(0, Array.IndexOf(DynModels, o.DynModel));
        ChkGga.IsChecked = o.Gga; ChkRmc.IsChecked = o.Rmc; ChkGsa.IsChecked = o.Gsa;
        ChkGsv.IsChecked = o.Gsv; ChkVtg.IsChecked = o.Vtg; ChkGll.IsChecked = o.Gll;
        CmbUartBaud.SelectedIndex = o.UartBaud is { } b ? Math.Max(0, new[] { 0, 9600, 38400, 115200, 230400 }.ToList().IndexOf(b)) : 0;
    }

    static string ItemText(ComboBox c) => ((ComboBoxItem)c.SelectedItem).Content.ToString()!;

    ReceiverOptions UiToOptions() => new()
    {
        Gps = ChkGps.IsChecked == true, Galileo = ChkGal.IsChecked == true, BeiDou = ChkBds.IsChecked == true,
        Glonass = ChkGlo.IsChecked == true, Qzss = ChkQzss.IsChecked == true, Sbas = ChkSbas.IsChecked == true,
        RateHz = Rates[Math.Max(0, CmbRate.SelectedIndex)], DynModel = DynModels[Math.Max(0, CmbDyn.SelectedIndex)],
        Gga = ChkGga.IsChecked == true, Rmc = ChkRmc.IsChecked == true, Gsa = ChkGsa.IsChecked == true,
        Gsv = ChkGsv.IsChecked == true, Vtg = ChkVtg.IsChecked == true, Gll = ChkGll.IsChecked == true,
        UartBaud = CmbUartBaud.SelectedIndex <= 0 ? null : int.Parse(ItemText(CmbUartBaud)),
    };

    static bool Num(string t, out double v) => double.TryParse(t.Trim().Replace(',', '.'), NumberStyles.Float, Inv, out v);

    bool UiToSettings()
    {
        if (!Num(TxtMinMove.Text, out var mm) || !Num(TxtTileMb.Text, out var mb) || !int.TryParse(TxtKmlPort.Text.Trim(), out var kp) || kp is < 1024 or > 65535)
        { TxtProgramMsg.Foreground = B("Bad"); TxtProgramMsg.Text = "Controlla i numeri: filtro e cache numerici, porta KML tra 1024 e 65535."; return false; }
        _s.RealPort = string.IsNullOrWhiteSpace(CmbRealPort.Text) ? null : CmbRealPort.Text.Trim();
        _s.RealBaud = CmbRealBaud.SelectedIndex <= 0 ? null : int.Parse(ItemText(CmbRealBaud));
        _s.Theme = ThemeName(CmbTheme.SelectedIndex);
        _s.Units = (UnitSystem)Math.Max(0, CmbUnits.SelectedIndex); _s.CoordFormat = (CoordFormat)Math.Max(0, CmbCoord.SelectedIndex);
        _s.GpxDir = TxtGpxDir.Text.Trim(); _s.MinMoveMeters = Math.Max(0, mm); _s.TileCacheMb = Math.Max(10, (long)mb);
        _s.KmlEnabled = ChkKml.IsChecked == true; _s.KmlFollow = ChkKmlFollow.IsChecked == true; _s.KmlPort = kp;
        _s.AutoStartRecording = ChkAutoRec.IsChecked == true; _s.StartWithWindows = ChkWinStart.IsChecked == true; _s.CheckUpdates = ChkUpdates.IsChecked == true;
        _s.ApplyProfileOnConnect = ChkApplyOnConnect.IsChecked == true;
        _s.Receiver = UiToOptions();
        return true;
    }

    void BtnSaveSettings_Click(object s, RoutedEventArgs e)
    {
        if (!UiToSettings()) return;
        _s.Save(AppSettings.DefaultPath);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (_s.StartWithWindows) key?.SetValue("GpsBridge", $"\"{Environment.ProcessPath}\"");
            else key?.DeleteValue("GpsBridge", false);
        }
        catch { }
        _tiles = NewTileCache();
        RestartBridge();
        _trackKey = ""; _lastSlow = DateTime.MinValue; RefreshWpGrid();
        TxtProgramMsg.Foreground = B("Good"); TxtProgramMsg.Text = "Impostazioni salvate e collegamento riavviato.";
    }

    static string ThemeName(int i) => i switch { 1 => "light", 2 => "auto", _ => "dark" };

    void CmbTheme_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || CmbTheme.SelectedIndex < 0) return;
        _s.Theme = ThemeName(CmbTheme.SelectedIndex);
        try { _s.Save(AppSettings.DefaultPath); } catch { }
        App.ApplyTheme(_s.Theme);
        PostMap(new { type = "theme", dark = App.IsDark });
        DrawProfile(); _lastSlow = DateTime.MinValue;
    }

    void BtnClearTiles_Click(object s, RoutedEventArgs e) { _tiles.Clear(); TxtProgramMsg.Foreground = B("Good"); TxtProgramMsg.Text = "Cache mappa svuotata."; }

    // ======================= ricevitore =======================
    void Log(string t) => TxtRxLog.Text = t + "\n" + TxtRxLog.Text;

    ReceiverController? Rx()
    {
        if (_bridge is null || (_bridge.Source is SerialSource ss && ss.State != ConnState.Connected))
        { Log("Ricevitore non collegato: attendi il collegamento e riprova."); return null; }
        return new ReceiverController(_bridge.Config, b => _bridge.Source.Write(b));
    }

    static string Describe(ApplyResult r) =>
        !r.Ok ? "ERRORE: " + r.Error
        : (r.AlreadyOk ? "Gia' a posto, nessuna scrittura. " : "Scritto. ") + "Attive: " + string.Join(", ", r.Active) +
          (r.Dropped.Count > 0 ? ". Non attivate: " + string.Join(", ", r.Dropped) + (r.Caps is { Simultaneous: > 0 } ? $" (il chip ne usa al massimo {r.Caps.Simultaneous} insieme)" : " (rifiutate dal ricevitore)") : "");

    async void BtnApplyBest_Click(object s, RoutedEventArgs e)
    {
        var rx = Rx(); if (rx is null) return;
        OptionsToUi(new ReceiverOptions());
        UiToSettings(); _s.Save(AppSettings.DefaultPath);
        Log("Profilo migliore (RAM+BBR+flash): " + Describe(await rx.ApplyBestAsync()));
    }

    async void BtnSaveFlash_Click(object s, RoutedEventArgs e)
    {
        var rx = Rx(); if (rx is null) return;
        UiToSettings(); _s.Save(AppSettings.DefaultPath);
        Log("Salvataggio in flash: " + Describe(await rx.ApplyAsync(_s.Receiver)));
    }

    async void BtnApplyRam_Click(object s, RoutedEventArgs e)
    {
        var rx = Rx(); if (rx is null) return;
        Log("Prova in RAM: " + Describe(await rx.ApplyAsync(UiToOptions(), UbxFrame.LayerRam)));
    }

    async void BtnReadRx_Click(object s, RoutedEventArgs e)
    {
        var rx = Rx(); if (rx is null) return;
        var st = await rx.ReadAsync(UiToOptions());
        var sb = new StringBuilder("Versione: " + st.Version + "\n");
        if (st.DiffFromOptions is null) sb.Append("Nessuna risposta alla lettura della configurazione.");
        else if (st.DiffFromOptions.IsEmpty) sb.Append("Il ricevitore corrisponde alle opzioni mostrate.");
        else
        {
            sb.Append("Differenze rispetto alle opzioni mostrate:\n");
            foreach (var d in st.DiffFromOptions.Items)
                sb.Append($"  0x{d.Key:X8}: voluto {d.Wanted}, nel ricevitore {(d.Actual?.ToString() ?? "non impostato")}\n");
        }
        Log(sb.ToString());
    }

    void BtnResetRx_Click(object s, RoutedEventArgs e)
    {
        var rx = Rx(); if (rx is null) return;
        if (MessageBox.Show(this, "Ripristinare i predefiniti u-blox (toglie le impostazioni salvate in flash e riavvia il ricevitore)?",
                "GpsBridge", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        rx.ResetToDefaults();
        Log("Predefiniti ripristinati: il ricevitore si riavvia. Alla prossima connessione l'app riapplica la configurazione se l'opzione e' attiva.");
    }
}
