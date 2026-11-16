using GpsBridge.Core.Nmea;
using Xunit;

namespace GpsBridge.Core.Tests;

public class NmeaSentenceTests
{
    public static string Make(string body) => $"${body}*{NmeaSentence.Checksum(body)}";

    [Fact]
    public void TryParse_valid_sentence()
    {
        var line = Make("GNGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,");
        Assert.True(NmeaSentence.TryParse(line, out var s));
        Assert.Equal("GN", s.Talker);
        Assert.Equal("GGA", s.Type);
        Assert.Equal("123519", s.Fields[0]);
    }

    [Fact]
    public void TryParse_bad_checksum_fails()
    {
        Assert.False(NmeaSentence.TryParse("$GNGGA,123519,4807.038,N*00", out _));
    }

    [Fact]
    public void TryParse_without_star_fails()
    {
        Assert.False(NmeaSentence.TryParse("$GNGGA,123519,4807.038,N", out _));
    }

    [Fact]
    public void Checksum_known_value()
    {
        Assert.Equal("47", NmeaSentence.Checksum("GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,"));
    }

    [Fact]
    public void WithTalker_rewrites_and_revalidates()
    {
        Assert.True(NmeaSentence.TryParse(Make("GBGSV,1,1,02,01,40,083,46,02,17,308,41"), out var s));
        var rewritten = s.WithTalker("GP");
        Assert.EndsWith("\r\n", rewritten);
        Assert.True(NmeaSentence.TryParse(rewritten, out var r));
        Assert.Equal("GP", r.Talker);
        Assert.Equal("GSV", r.Type);
        Assert.Equal(s.Fields, r.Fields);
    }
}
