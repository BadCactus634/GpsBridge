using System.IO;
using System.Windows;
using GpsBridge.Core.Settings;
using Microsoft.Win32;

namespace GpsBridge.App;

public partial class App : Application
{
    /// <summary>Stesso nome di AppMutex nell'installer: l'installer sa se l'app e' in esecuzione.</summary>
    public const string MutexName = "GpsBridgeSingleInstance";
    static Mutex? _mutex;

    /// <summary>true se il tema attualmente applicato e' quello scuro.</summary>
    public static bool IsDark { get; private set; } = true;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Una sola copia alla volta (due copie si contenderebbero la porta del ricevitore).
        // Dopo un aggiornamento l'installer rilancia l'app con /RELAUNCH=1: si aspetta che la vecchia finisca.
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            bool relaunch = e.Args.Any(a => a.StartsWith("/RELAUNCH", StringComparison.OrdinalIgnoreCase));
            bool got = false;
            if (relaunch) { try { got = _mutex.WaitOne(15000); } catch (AbandonedMutexException) { got = true; } }
            if (!got)
            {
                MessageBox.Show("GpsBridge e' gia' aperto.", "GpsBridge", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(); return;
            }
        }

        DispatcherUnhandledException += (_, ev) =>
        {
            Log("Errore nell'interfaccia", ev.Exception);
            MessageBox.Show("Si e' verificato un errore imprevisto:\n\n" + ev.Exception.Message +
                "\n\nI dettagli sono in " + LogPath + "\nL'app continua a funzionare; se il problema si ripete, riaprila.",
                "GpsBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log("Errore fatale", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log("Errore in un'operazione in background", ev.Exception); ev.SetObserved(); };

        ApplyTheme(AppSettings.Load(AppSettings.DefaultPath).Theme);
        base.OnStartup(e);
    }

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GpsBridge", "errori.log");

    public static void Log(string what, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 512 * 1024) File.Delete(LogPath);   // niente log infiniti
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what} (v{AboutDialog.Version})\n{ex}\n\n");
        }
        catch { /* il log non deve mai causare un altro errore */ }
    }

    /// <summary>name: "dark", "light" oppure "auto" (segue il tema di Windows). Sostituisce il dizionario dei colori a caldo.</summary>
    public static void ApplyTheme(string name)
    {
        bool dark = name switch { "light" => false, "dark" => true, _ => !WindowsUsesLight() };
        IsDark = dark;
        var dict = new ResourceDictionary { Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        var merged = Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = dict; else merged.Add(dict);
    }

    static bool WindowsUsesLight()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is int v && v == 1; }
        catch { return false; }
    }
}
