namespace GpsBridge.Core.Ubx;

public static class UbxFrame
{
    public const byte LayerRam = 1, LayerBbr = 2, LayerFlash = 4;     // bitmask di VALSET
    public const byte GetRam = 0, GetBbr = 1, GetFlash = 2, GetDefault = 7; // layer di VALGET

    public static byte[] Build(byte cls, byte id, byte[] payload)
    {
        var f = new byte[payload.Length + 8];
        f[0] = 0xB5; f[1] = 0x62; f[2] = cls; f[3] = id;
        f[4] = (byte)(payload.Length & 0xFF); f[5] = (byte)(payload.Length >> 8);
        payload.CopyTo(f, 6);
        byte a = 0, b = 0;
        for (int i = 2; i < f.Length - 2; i++) { a += f[i]; b += a; }
        f[^2] = a; f[^1] = b;
        return f;
    }

    public static bool TryRead(byte[] f, out byte cls, out byte id, out byte[] payload)
    {
        cls = id = 0; payload = Array.Empty<byte>();
        if (f.Length < 8 || f[0] != 0xB5 || f[1] != 0x62) return false;
        int len = f[4] | (f[5] << 8);
        if (f.Length != len + 8) return false;
        byte a = 0, b = 0;
        for (int i = 2; i < f.Length - 2; i++) { a += f[i]; b += a; }
        if (a != f[^2] || b != f[^1]) return false;
        cls = f[2]; id = f[3];
        payload = f.AsSpan(6, len).ToArray();
        return true;
    }

    public static byte[] MonVer() => Build(0x0A, 0x04, Array.Empty<byte>());

    public static byte[] ValSet(byte layers, IEnumerable<(uint Key, ulong Value)> items)
    {
        var p = new List<byte> { 0, layers, 0, 0 };
        foreach (var (key, value) in items)
        {
            p.AddRange(BitConverter.GetBytes(key));
            int n = UbxKeys.SizeOf(key);
            for (int i = 0; i < n; i++) p.Add((byte)(value >> (8 * i)));
        }
        return Build(0x06, 0x8A, p.ToArray());
    }

    public static byte[] ValGet(byte layer, IEnumerable<uint> keys)
    {
        var p = new List<byte> { 0, layer, 0, 0 };
        foreach (var k in keys) p.AddRange(BitConverter.GetBytes(k));
        return Build(0x06, 0x8B, p.ToArray());
    }

    /// <summary>Risposta CFG-VALGET (payload) -> coppie chiave/valore.</summary>
    public static Dictionary<uint, ulong> ParseValGet(byte[] payload)
    {
        var r = new Dictionary<uint, ulong>();
        int i = 4;
        while (i + 4 <= payload.Length)
        {
            uint key = BitConverter.ToUInt32(payload, i); i += 4;
            int n = UbxKeys.SizeOf(key);
            if (i + n > payload.Length) break;
            ulong v = 0;
            for (int k = 0; k < n; k++) v |= (ulong)payload[i + k] << (8 * k);
            i += n;
            r[key] = v;
        }
        return r;
    }
}
