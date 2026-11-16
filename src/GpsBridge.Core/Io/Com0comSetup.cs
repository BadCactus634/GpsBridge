using System.Diagnostics;
using System.IO.Ports;

namespace GpsBridge.Core.Io;

/// <summary>Rileva e (con UAC) crea la coppia di porte COM virtuali com0com usata da Google Earth.</summary>
public static class Com0comSetup
{
    public static string InstallArgs(string a, string b) => $"install PortName={a} PortName={b}";

    public static string? FindSetupc(IEnumerable<string> dirs)
    {
        foreach (var d in dirs)
        {
            var p = Path.Combine(d, "setupc.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static IEnumerable<string> DefaultDirs() => new[]
    {
        Path.Combine(AppContext.BaseDirectory, "com0com"),
        @"C:\Program Files (x86)\com0com",
        @"C:\Program Files\com0com",
    };

    public static bool IsInstalled() => FindSetupc(DefaultDirs()) is not null;

    public static bool PairExists(string a, string b)
    {
        var names = SerialPort.GetPortNames();
        return names.Contains(a) && names.Contains(b);
    }

    /// <summary>Crea la coppia se manca. Mostra la richiesta UAC; attende il termine di setupc.exe.</summary>
    public static (bool Ok, string Message) EnsurePair(string a = "COM20", string b = "COM21")
    {
        if (PairExists(a, b)) return (true, $"Coppia {a}<->{b} gia' presente.");
        var setupc = FindSetupc(DefaultDirs());
        if (setupc is null) return (false, "com0com non e' installato (setupc.exe non trovato): reinstalla GpsBridge o installa com0com.");
        try
        {
            var psi = new ProcessStartInfo(setupc, InstallArgs(a, b))
            {
                WorkingDirectory = Path.GetDirectoryName(setupc)!,
                Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(30000);
            return PairExists(a, b) ? (true, $"Creata la coppia {a}<->{b}.")
                                    : (false, "setupc.exe terminato ma le porte non compaiono: controlla il driver com0com.");
        }
        catch (Exception e)
        {
            return (false, "Creazione della coppia COM virtuale non riuscita (UAC rifiutato?): " + e.Message);
        }
    }
}
