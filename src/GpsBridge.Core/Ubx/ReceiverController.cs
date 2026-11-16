using GpsBridge.Core.Settings;

namespace GpsBridge.Core.Ubx;

public sealed record ReceiverStatus(string Version, ConfigDiff? DiffFromOptions);

/// <summary>Azioni della sezione «Ricevitore» delle impostazioni, sopra UbxConfigurator.</summary>
public sealed class ReceiverController
{
    public const byte AllLayers = UbxFrame.LayerRam | UbxFrame.LayerBbr | UbxFrame.LayerFlash;

    readonly UbxConfigurator _cfg;
    readonly Action<byte[]> _send;

    public ReceiverController(UbxConfigurator cfg, Action<byte[]> send) { _cfg = cfg; _send = send; }

    /// <summary>Scrive le opzioni nei layer indicati (solo RAM = prova; tutti = salvataggio in flash).</summary>
    public Task<ApplyResult> ApplyAsync(ReceiverOptions o, byte layers = AllLayers) =>
        _cfg.ApplyAsync(ReceiverProfile.FromOptions(o), layers);

    public Task<ApplyResult> ApplyBestAsync() => ApplyAsync(new ReceiverOptions());

    /// <summary>Versione del firmware e differenze fra il ricevitore (RAM) e le opzioni correnti.</summary>
    public async Task<ReceiverStatus> ReadAsync(ReceiverOptions o)
    {
        var version = await _cfg.ReadVersionAsync();
        var p = ReceiverProfile.FromOptions(o);
        var diff = await _cfg.DiffAsync(p, p.Constellations.Count, UbxFrame.GetRam);
        return new ReceiverStatus(version, diff);
    }

    /// <summary>Toglie le nostre chiavi da BBR e flash e riavvia il ricevitore: tornano i default u-blox.</summary>
    public void ResetToDefaults()
    {
        _send(UbxFrame.ValDel(UbxFrame.LayerBbr | UbxFrame.LayerFlash, ReceiverProfile.ManagedKeys));
        _send(UbxFrame.Rst());
    }
}
