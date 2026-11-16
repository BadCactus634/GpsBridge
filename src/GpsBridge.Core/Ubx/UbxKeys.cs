using GpsBridge.Core.Settings;

namespace GpsBridge.Core.Ubx;

/// <summary>
/// Chiavi di configurazione u-blox M10 (CFG-*). ATTENZIONE: gli ID sono scritti a memoria dal manuale
/// «u-blox M10 interface description»: vanno confermati sul ricevitore con VALGET (vedi Task 13 del piano).
/// </summary>
public static class UbxKeys
{
    // Segnali / costellazioni
    public const uint GpsEna = 0x1031001f, GpsL1caEna = 0x10310001;
    public const uint SbasEna = 0x10310020, SbasL1caEna = 0x10310005;
    public const uint GalEna = 0x10310021, GalE1Ena = 0x10310007;
    public const uint BdsEna = 0x10310022, BdsB1Ena = 0x1031000d, BdsB1cEna = 0x1031000f;
    public const uint QzssEna = 0x10310024, QzssL1caEna = 0x10310012;
    public const uint GloEna = 0x10310025, GloL1Ena = 0x10310018;

    // Frequenza, modello dinamico, porta
    public const uint RateMeas = 0x30210001, RateNav = 0x30210002;
    public const uint DynModel = 0x20110021;
    public const uint Uart1Baud = 0x40520001, Uart1OutNmea = 0x10740002;

    // Messaggi NMEA su UART1 (valore U1 = ogni quante epoche)
    public const uint MsgGga = 0x209100bb, MsgGll = 0x209100ca, MsgGsa = 0x209100c0,
                      MsgGsv = 0x209100c5, MsgRmc = 0x209100ac, MsgVtg = 0x209100b1;

    /// <summary>Dimensione del valore in byte dai bit 28-30 dell'ID chiave.</summary>
    public static int SizeOf(uint key) => ((key >> 28) & 7) switch
    {
        1 or 2 => 1,
        3 => 2,
        4 => 4,
        5 => 8,
        _ => 1,
    };
}

public sealed record ConstellationGroup(string Name, (uint Key, ulong Value)[] Enable, (uint Key, ulong Value)[] Disable);

public sealed class ReceiverProfile
{
    /// <summary>In ordine di priorita': in caso di NAK si tolgono da coda.</summary>
    public IReadOnlyList<ConstellationGroup> Constellations { get; init; } = Array.Empty<ConstellationGroup>();
    public IReadOnlyList<(uint Key, ulong Value)> Other { get; init; } = Array.Empty<(uint, ulong)>();

    /// <summary>Elenco da inviare con le prime <paramref name="enabledGroups"/> costellazioni accese e le altre spente.</summary>
    public IEnumerable<(uint Key, ulong Value)> Flatten(int enabledGroups)
    {
        for (int i = 0; i < Constellations.Count; i++)
            foreach (var kv in i < enabledGroups ? Constellations[i].Enable : Constellations[i].Disable) yield return kv;
        foreach (var kv in Other) yield return kv;
    }

    static ConstellationGroup G(string name, uint parent, params (uint, ulong)[] sub) =>
        new(name, new[] { (parent, 1UL) }.Concat(sub).ToArray(), new[] { (parent, 0UL) });

    /// <summary>Profilo «migliore»: tutte le costellazioni ammesse, 5 Hz, modello portatile, NMEA completo. Baud invariato.</summary>
    public static ReceiverProfile Best => FromOptions(new ReceiverOptions());

    /// <summary>Ordine di preferenza (Europa) fra le costellazioni principali quando il chip ne puo' usare meno di quelle richieste.</summary>
    static readonly string[] MainPriority = { "GPS", "Galileo", "GLONASS", "BeiDou" };

    /// <summary>
    /// Profilo con al massimo <paramref name="max"/> costellazioni principali (GPS, Galileo, GLONASS, BeiDou) accese; le altre
    /// vengono spente esplicitamente. QZSS e SBAS non contano. Restituisce anche i nomi di quelle tolte.
    /// </summary>
    public (ReceiverProfile Profile, IReadOnlyList<string> Limited) WithMaxMain(int max)
    {
        var mains = Constellations.Where(c => MainPriority.Contains(c.Name)).OrderBy(c => Array.IndexOf(MainPriority, c.Name)).ToList();
        if (mains.Count <= max) return (this, Array.Empty<string>());
        var drop = mains.Skip(max).ToList();
        var keep = Constellations.Where(c => !drop.Contains(c)).ToList();
        var other = Other.Concat(drop.SelectMany(d => d.Disable)).ToList();
        return (new ReceiverProfile { Constellations = keep, Other = other }, drop.Select(d => d.Name).ToList());
    }

    /// <summary>Tutte le chiavi che l'app puo' scrivere (per il ripristino dei default).</summary>
    public static IReadOnlyList<uint> ManagedKeys { get; } =
        FromOptions(new ReceiverOptions { UartBaud = 115200 }).Flatten(6).Select(x => x.Key).Distinct().ToList();

    public static ReceiverProfile FromOptions(ReceiverOptions o)
    {
        // Ordine di priorita' (da coda si scende in caso di NAK); quelle spente a mano non entrano nell'elenco e vengono spente in Other.
        var all = new (bool On, ConstellationGroup G)[]
        {
            (o.Gps, G("GPS", UbxKeys.GpsEna, (UbxKeys.GpsL1caEna, 1))),
            (o.Galileo, G("Galileo", UbxKeys.GalEna, (UbxKeys.GalE1Ena, 1))),
            (o.BeiDou, G("BeiDou", UbxKeys.BdsEna, (UbxKeys.BdsB1cEna, 1), (UbxKeys.BdsB1Ena, 0))), // B1C: B1I non convive con GLONASS
            (o.Glonass, G("GLONASS", UbxKeys.GloEna, (UbxKeys.GloL1Ena, 1))),
            (o.Qzss, G("QZSS", UbxKeys.QzssEna, (UbxKeys.QzssL1caEna, 1))),
            (o.Sbas, G("SBAS", UbxKeys.SbasEna, (UbxKeys.SbasL1caEna, 1))),
        };
        var other = new List<(uint, ulong)>();
        foreach (var (on, g) in all) if (!on) other.AddRange(g.Disable);
        other.Add((UbxKeys.RateMeas, (ulong)(1000 / Math.Clamp(o.RateHz, 1, 10))));
        other.Add((UbxKeys.DynModel, (ulong)o.DynModel));
        other.Add((UbxKeys.Uart1OutNmea, 1));
        void Msg(uint key, bool on) => other.Add((key, on ? 1UL : 0UL));
        Msg(UbxKeys.MsgGga, o.Gga); Msg(UbxKeys.MsgGll, o.Gll); Msg(UbxKeys.MsgGsa, o.Gsa);
        Msg(UbxKeys.MsgGsv, o.Gsv); Msg(UbxKeys.MsgRmc, o.Rmc); Msg(UbxKeys.MsgVtg, o.Vtg);
        if (o.UartBaud is { } baud) other.Add((UbxKeys.Uart1Baud, (ulong)baud));   // sempre per ultima: cambia la velocita' del collegamento
        return new ReceiverProfile { Constellations = all.Where(x => x.On).Select(x => x.G).ToArray(), Other = other };
    }
}