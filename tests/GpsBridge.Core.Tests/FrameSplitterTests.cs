using System.Text;
using GpsBridge.Core.Nmea;
using Xunit;

namespace GpsBridge.Core.Tests;

public class FrameSplitterTests
{
    static string Line(string body) => NmeaSentenceTests.Make(body) + "\r\n";

    static byte[] UbxAck()
    {
        // UBX-ACK-ACK per CFG-VALSET (06 8A)
        byte[] core = { 0x05, 0x01, 0x02, 0x00, 0x06, 0x8A };
        byte a = 0, b = 0;
        foreach (var x in core) { a += x; b += a; }
        var f = new List<byte> { 0xB5, 0x62 };
        f.AddRange(core); f.Add(a); f.Add(b);
        return f.ToArray();
    }

    static (List<NmeaSentence> nmea, List<byte[]> ubx, List<string> bad, FrameSplitter fs) Make()
    {
        var nmea = new List<NmeaSentence>(); var ubx = new List<byte[]>(); var bad = new List<string>();
        var fs = new FrameSplitter();
        fs.Nmea += nmea.Add; fs.Ubx += ubx.Add; fs.BadLine += bad.Add;
        return (nmea, ubx, bad, fs);
    }

    [Fact]
    public void Data_split_at_every_byte_gives_same_sentences()
    {
        var (nmea, _, _, fs) = Make();
        var bytes = Encoding.ASCII.GetBytes(Line("GPGGA,1,2,3") + Line("GNRMC,4,5,6"));
        foreach (var b in bytes) fs.Feed(new[] { b });
        Assert.Equal(new[] { "GGA", "RMC" }, nmea.Select(n => n.Type));
    }

    [Fact]
    public void Ubx_between_nmea_does_not_damage_neighbours()
    {
        var (nmea, ubx, bad, fs) = Make();
        var all = Encoding.ASCII.GetBytes(Line("GPGGA,1,2,3")).Concat(UbxAck()).Concat(Encoding.ASCII.GetBytes(Line("GNRMC,4,5,6"))).ToArray();
        fs.Feed(all);
        Assert.Equal(2, nmea.Count);
        Assert.Single(ubx);
        Assert.Equal(0x05, ubx[0][2]);
        Assert.Empty(bad);
    }

    [Fact]
    public void Bad_checksum_raises_BadLine_and_no_sentence()
    {
        var (nmea, _, bad, fs) = Make();
        fs.Feed(Encoding.ASCII.GetBytes("$GPGGA,1,2,3*00\r\n"));
        Assert.Empty(nmea);
        Assert.Single(bad);
    }

    [Fact]
    public void Truncated_line_then_new_dollar_resyncs()
    {
        var (nmea, _, bad, fs) = Make();
        fs.Feed(Encoding.ASCII.GetBytes("$GPGGA,1,2" + Line("GNRMC,4,5,6")));
        Assert.Single(nmea);
        Assert.Equal("RMC", nmea[0].Type);
        Assert.Single(bad);
    }

    [Fact]
    public void Garbage_before_dollar_is_ignored()
    {
        var (nmea, _, bad, fs) = Make();
        fs.Feed(new byte[] { 0x00, 0xFF, 0x41 });
        fs.Feed(Encoding.ASCII.GetBytes(Line("GPGGA,1,2,3")));
        Assert.Single(nmea);
        Assert.Empty(bad);
    }
}
