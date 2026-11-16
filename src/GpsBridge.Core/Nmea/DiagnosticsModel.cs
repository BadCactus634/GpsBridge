namespace GpsBridge.Core.Nmea;

public sealed record ConstellationRow(string Name, int Used, int InView, double? AvgSnr);
/// <summary>X,Y in [-1,1]: centro = zenit, nord in alto.</summary>
public sealed record SkyPoint(string Constellation, int Prn, double X, double Y, int? Snr, bool Used);

/// <summary>Trasformazioni pure dello stato GNSS per la scheda Diagnostica.</summary>
public static class DiagnosticsModel
{
    static readonly string[] Order = { "GPS", "Galileo", "BeiDou", "GLONASS", "QZSS" };

    public static IReadOnlyList<ConstellationRow> Rows(GnssState st) =>
        st.Satellites.GroupBy(s => s.Constellation)
            .Select(g =>
            {
                var snrs = g.Where(x => x.Snr is > 0).Select(x => (double)x.Snr!.Value).ToList();
                return new ConstellationRow(g.Key, g.Count(x => x.Used), g.Count(), snrs.Count > 0 ? snrs.Average() : null);
            })
            .OrderBy(r => Array.IndexOf(Order, r.Name) is var i and >= 0 ? i : 99).ToList();

    public static IReadOnlyList<SkyPoint> Sky(GnssState st) =>
        st.Satellites.Where(s => s.Elev is not null && s.Azim is not null)
            .Select(s =>
            {
                double r = (90 - Math.Clamp(s.Elev!.Value, 0, 90)) / 90.0;
                double a = s.Azim!.Value * Math.PI / 180;
                return new SkyPoint(s.Constellation, s.Prn, r * Math.Sin(a), r * Math.Cos(a), s.Snr, s.Used);
            }).ToList();

    public static string FixText(int quality) => quality switch
    {
        0 => "Nessun fix", 1 => "Fix GPS", 2 => "Fix DGPS/SBAS", 3 => "PPS", 4 => "RTK fisso", 5 => "RTK float", 6 => "Stimato",
        _ => "Fix " + quality,
    };
}
