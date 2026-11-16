using GpsBridge.Core.Ubx;
using Xunit;

namespace GpsBridge.Core.Tests;

public class UbxFrameTests
{
    [Fact]
    public void MonVer_matches_known_vector()
    {
        Assert.Equal(new byte[] { 0xB5, 0x62, 0x0A, 0x04, 0x00, 0x00, 0x0E, 0x34 }, UbxFrame.MonVer());
    }

    [Fact]
    public void Build_and_TryRead_round_trip()
    {
        var f = UbxFrame.Build(0x06, 0x8A, new byte[] { 1, 2, 3 });
        Assert.True(UbxFrame.TryRead(f, out var cls, out var id, out var payload));
        Assert.Equal((byte)0x06, cls); Assert.Equal((byte)0x8A, id);
        Assert.Equal(new byte[] { 1, 2, 3 }, payload);
        f[^1] ^= 0xFF;
        Assert.False(UbxFrame.TryRead(f, out _, out _, out _));
    }

    [Fact]
    public void ValSet_encodes_value_sizes_from_key()
    {
        var f = UbxFrame.ValSet(UbxFrame.LayerRam | UbxFrame.LayerFlash,
            new (uint, ulong)[] { (UbxKeys.GpsEna, 1), (UbxKeys.Uart1Baud, 115200) });
        Assert.True(UbxFrame.TryRead(f, out var cls, out var id, out var p));
        Assert.Equal((byte)0x06, cls); Assert.Equal((byte)0x8A, id);
        Assert.Equal(4 + (4 + 1) + (4 + 4), p.Length);
        Assert.Equal(0, p[0]);              // version
        Assert.Equal(0b101, p[1]);          // RAM | Flash
        Assert.Equal(0x1f, p[4]);           // GPS_ENA key LE
        Assert.Equal(1, p[8]);              // L value
        Assert.Equal(115200u, BitConverter.ToUInt32(p, 13));
    }

    [Fact]
    public void ValGet_lists_keys()
    {
        var f = UbxFrame.ValGet(UbxFrame.GetRam, new[] { UbxKeys.RateMeas });
        Assert.True(UbxFrame.TryRead(f, out _, out var id, out var p));
        Assert.Equal((byte)0x8B, id);
        Assert.Equal(4 + 4, p.Length);
        Assert.Equal(0, p[1]);
    }

    [Fact]
    public void Best_profile_uses_B1C_never_B1I_with_GLONASS()
    {
        var items = ReceiverProfile.Best.Flatten(6).ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal(1UL, items[UbxKeys.GloEna]);
        Assert.Equal(1UL, items[UbxKeys.BdsB1cEna]);
        Assert.Equal(0UL, items[UbxKeys.BdsB1Ena]);
    }

    [Fact]
    public void Flatten_with_fewer_groups_disables_the_tail()
    {
        var items = ReceiverProfile.Best.Flatten(3).ToDictionary(x => x.Key, x => x.Value);
        Assert.Equal(1UL, items[UbxKeys.GpsEna]);
        Assert.Equal(0UL, items[UbxKeys.GloEna]);
        Assert.Equal(0UL, items[UbxKeys.SbasEna]);
    }
}
