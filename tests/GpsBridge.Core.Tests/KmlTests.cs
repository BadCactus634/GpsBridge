using System.Net.Http;
using System.Xml.Linq;
using GpsBridge.Core.Kml;
using GpsBridge.Core.Nmea;
using Xunit;

namespace GpsBridge.Core.Tests;

public class KmlTests
{
    static GnssFix Fix(double lat, double lon) =>
        new(lat, lon, 120.5, new DateTimeOffset(2026, 1, 16, 12, 0, 0, TimeSpan.Zero), 10, 90);

    static XDocument P(string s) => XDocument.Parse(s);
    static IEnumerable<XElement> E(XDocument d, string n) => d.Descendants().Where(e => e.Name.LocalName == n);

    [Fact]
    public void Position_uses_lon_lat_alt_order_and_invariant_decimals()
    {
        var d = P(KmlBuilder.Position(Fix(45.5, 9.25), new[] { (45.4, 9.2), (45.5, 9.25) }, "Fix GPS", true));
        Assert.Equal("9.2500000,45.5000000,120.5", E(d, "Point").Single().Elements().First(x => x.Name.LocalName == "coordinates").Value);
        Assert.Contains("9.2000000,45.4000000,0", E(d, "LineString").Single().Value);
    }

    [Fact]
    public void No_fix_gives_valid_kml_without_placemarks()
    {
        var d = P(KmlBuilder.Position(null, Array.Empty<(double, double)>(), "Nessun fix", true));
        Assert.Empty(E(d, "Placemark"));
        Assert.Single(E(d, "Document"));
    }

    [Fact]
    public void Link_file_points_to_localhost_with_one_second_refresh_and_follow()
    {
        var d = P(KmlBuilder.NetworkLink(8765, follow: true));
        Assert.Equal("http://127.0.0.1:8765/position.kml", E(d, "href").Single().Value);
        Assert.Equal("1", E(d, "refreshInterval").Single().Value);
        Assert.Equal("1", E(d, "flyToView").Single().Value);
        Assert.Equal("0", E(P(KmlBuilder.NetworkLink(8765, follow: false)), "flyToView").Single().Value);
    }

    [Fact]
    public async Task Server_serves_both_files_and_updates_position()
    {
        using var srv = new KmlServer(0);
        srv.Start();
        using var http = new HttpClient();
        var link = await http.GetStringAsync($"http://127.0.0.1:{srv.Port}/live.kml");
        Assert.Contains($":{srv.Port}/position.kml", link);
        Assert.Empty(E(P(await http.GetStringAsync($"http://127.0.0.1:{srv.Port}/position.kml")), "Placemark"));
        srv.Update(Fix(45.5, 9.25), "Fix GPS");
        Assert.NotEmpty(E(P(await http.GetStringAsync($"http://127.0.0.1:{srv.Port}/position.kml")), "Placemark"));
        var r = await http.GetAsync($"http://127.0.0.1:{srv.Port}/altro");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, r.StatusCode);
    }
}
