using System.Text;
using GpsBridge.Core.Io;
using GpsBridge.Core.Nmea;
using Xunit;

namespace GpsBridge.Core.Tests;

sealed class CaptureStream : IByteStream
{
    public readonly StringBuilder Text = new();
    public bool Throw;
    public event Action<byte[]>? DataReceived { add { } remove { } }
    public void Write(ReadOnlySpan<byte> data)
    {
        if (Throw) throw new IOException("COM21 non apre");
        Text.Append(Encoding.ASCII.GetString(data));
    }
    public string[] Lines => Text.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
}

public class EarthWriterTests
{
    static NmeaSentence S(string body) { Assert.True(NmeaSentence.TryParse(NmeaSentenceTests.Make(body), out var s)); return s; }

    [Fact]
    public void Ten_hz_in_gives_one_hz_out_with_whole_epochs()
    {
        var cap = new CaptureStream(); var t = new DateTime(2026, 1, 16, 12, 0, 0, DateTimeKind.Utc);
        var w = new EarthWriter(cap, hz: 1, clock: () => t);
        for (int i = 0; i < 30; i++)
        {
            w.OnSentence(S("GNRMC,1,A,2,N,3,E,0,0,160126,,,A"));
            w.OnSentence(S("GNGGA,1,2,N,3,E,1,08,0.9,5.0,M,0,M,,"));
            w.OnSentence(S("GNGSA,A,3,01,02,,,,,,,,,,,1.8,0.9,1.5,1"));
            t = t.AddMilliseconds(100);
        }
        Assert.Equal(3, cap.Lines.Count(l => l.Contains("GPGGA")));
        Assert.Equal(3, cap.Lines.Count(l => l.Contains("GPRMC")));
        Assert.Equal(3, cap.Lines.Count(l => l.Contains("GPGSA")));
    }

    [Fact]
    public void Talkers_are_GP_and_checksums_valid_and_gsa_loses_system_id()
    {
        var cap = new CaptureStream();
        var w = new EarthWriter(cap, hz: 1);
        w.OnSentence(S("GNGGA,1,2,N,3,E,1,08,0.9,5.0,M,0,M,,"));
        w.OnSentence(S("GBGSV,1,1,02,11,30,090,35,12,15,250,31,1"));
        w.OnSentence(S("GNGSA,A,3,01,02,,,,,,,,,,,1.8,0.9,1.5,1"));
        foreach (var l in cap.Lines)
        {
            Assert.True(NmeaSentence.TryParse(l, out var r));
            Assert.Equal("GP", r.Talker);
        }
        NmeaSentence.TryParse(cap.Lines.Single(l => l.Contains("GSA")), out var gsa);
        Assert.Equal(17, gsa!.Fields.Length);
    }

    [Fact]
    public void Other_sentences_are_dropped()
    {
        var cap = new CaptureStream();
        var w = new EarthWriter(cap, hz: 1);
        w.OnSentence(S("GNTXT,01,01,02,u-blox"));
        w.OnSentence(S("GNZDA,1,2,3,4,5,6"));
        Assert.Empty(cap.Lines);
    }

    [Fact]
    public void Output_port_failure_sets_status_and_does_not_throw()
    {
        var cap = new CaptureStream { Throw = true };
        var w = new EarthWriter(cap, hz: 1);
        w.OnSentence(S("GNGGA,1,2,N,3,E,1,08,0.9,5.0,M,0,M,,"));
        Assert.False(w.Status.Available);
        Assert.Contains("COM virtuale", w.Status.Error);
    }
}

public class Com0comSetupTests
{
    [Fact]
    public void Install_args()
    {
        Assert.Equal("install PortName=COM20 PortName=COM21", Com0comSetup.InstallArgs("COM20", "COM21"));
    }

    [Fact]
    public void FindSetupc_returns_null_when_absent_and_path_when_present()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gpsb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(Com0comSetup.FindSetupc(new[] { dir }));
            File.WriteAllText(Path.Combine(dir, "setupc.exe"), "x");
            Assert.Equal(Path.Combine(dir, "setupc.exe"), Com0comSetup.FindSetupc(new[] { dir }));
        }
        finally { Directory.Delete(dir, true); }
    }
}
