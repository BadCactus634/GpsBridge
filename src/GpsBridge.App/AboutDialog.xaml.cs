using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace GpsBridge.App;

/// <summary>Versione, data di compilazione e crediti (letti dall'assembly: si aggiornano da soli a ogni build).</summary>
public partial class AboutDialog : Window
{
    public static string Version =>
        (Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0)).ToString(3);

    public static string BuildDate =>
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "n.d.";

    public static string Summary => $"GpsBridge {Version} (compilata il {BuildDate}) - .NET {Environment.Version}, Windows {Environment.OSVersion.Version}";

    public AboutDialog()
    {
        InitializeComponent();
        TxtVersion.Text = "versione " + Version;
        TxtBuild.Text = "compilata il " + BuildDate;
    }

    void Link_Navigate(object s, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }

    /// <summary>Impostata da chi apre il box: esegue il controllo e restituisce il messaggio da mostrare.</summary>
    public Func<Task<string>>? CheckForUpdates { get; set; }

    async void Check_Click(object s, RoutedEventArgs e)
    {
        if (CheckForUpdates is null) return;
        BtnCheck.IsEnabled = false; TxtUpd.Text = "Controllo in corso...";
        try { TxtUpd.Text = await CheckForUpdates(); } finally { BtnCheck.IsEnabled = true; }
    }

    void Copy_Click(object s, RoutedEventArgs e) { try { Clipboard.SetText(Summary); } catch { } }
}
