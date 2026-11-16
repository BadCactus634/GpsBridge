using System.Net;
using System.Security.Cryptography;
using System.Text;
using GpsBridge.Core.Update;
using Xunit;

namespace GpsBridge.Core.Tests;

sealed class FakeHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
    public List<string> Urls = new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    { Urls.Add(r.RequestUri!.ToString()); return Task.FromResult(Respond(r)); }
}

public class UpdateCheckerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "gpsb-up-" + Guid.NewGuid().ToString("N"));
    public UpdateCheckerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    static string Json(string tag, string? digest = null, string asset = "GpsBridge-setup.exe", bool pre = false, long size = 5) => $$"""
    {"tag_name":"{{tag}}","name":"GpsBridge","html_url":"https://github.com/BadCactus634/GpsBridge/releases/tag/{{tag}}",
     "prerelease":{{(pre ? "true" : "false")}},"draft":false,"body":"Novita' della versione",
     "assets":[{"name":"{{asset}}","size":{{size}},"browser_download_url":"https://github.com/BadCactus634/GpsBridge/releases/download/{{tag}}/{{asset}}"{{(digest is null ? "" : $",\"digest\":\"{digest}\"")}}}]}
    """;

    static UpdateChecker Make(FakeHandler h) => new(new HttpClient(h), "BadCactus634/GpsBridge");

    [Fact]
    public async Task Newer_release_is_reported_with_asset_notes_and_digest()
    {
        var h = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json("v1.2.0", "sha256:ABCDEF")) } };
        var u = await Make(h).CheckAsync(new Version(1, 1, 0));
        Assert.NotNull(u);
        Assert.Equal(new Version(1, 2, 0), u!.Version);
        Assert.EndsWith("/v1.2.0/GpsBridge-setup.exe", u.DownloadUrl);
        Assert.Equal("Novita' della versione", u.Notes);
        Assert.Equal("abcdef", u.Sha256);
        Assert.Contains("repos/BadCactus634/GpsBridge/releases/latest", h.Urls[0]);
    }

    [Theory]
    [InlineData("v1.1.0")]   // uguale
    [InlineData("v1.0.9")]   // piu' vecchia
    public async Task Same_or_older_release_gives_null(string tag)
    {
        var h = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json(tag)) } };
        Assert.Null(await Make(h).CheckAsync(new Version(1, 1, 0)));
    }

    [Fact]
    public async Task Prerelease_missing_asset_bad_tag_and_http_errors_give_null_never_throw()
    {
        foreach (var body in new[] { Json("v9.0.0", pre: true), Json("v9.0.0", asset: "altro.zip"), Json("beta") })
        {
            var h = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) } };
            Assert.Null(await Make(h).CheckAsync(new Version(1, 1, 0)));
        }
        Assert.Null(await Make(new FakeHandler()).CheckAsync(new Version(1, 1, 0)));                    // 404
        Assert.Null(await Make(new FakeHandler { Respond = _ => throw new HttpRequestException("offline") }).CheckAsync(new Version(1, 1, 0)));
        Assert.Null(await Make(new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("non json") } }).CheckAsync(new Version(1, 1, 0)));
    }

    [Fact]
    public async Task Download_verifies_sha256_and_refuses_a_tampered_file()
    {
        var data = Encoding.ASCII.GetBytes("INSTALLER");
        string good = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var h = new FakeHandler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) } };
        var c = Make(h);
        var path = Path.Combine(_dir, "setup.exe");
        var ok = new UpdateInfo(new Version(1, 2, 0), "https://github.com/x/y/releases/download/v1.2.0/GpsBridge-setup.exe", "n", "https://github.com/x", good, data.Length);
        await c.DownloadAsync(ok, path);
        Assert.Equal(data, File.ReadAllBytes(path));

        var bad = ok with { Sha256 = new string('0', 64) };
        var p2 = Path.Combine(_dir, "bad.exe");
        await Assert.ThrowsAsync<InvalidDataException>(() => c.DownloadAsync(bad, p2));
        Assert.False(File.Exists(p2));
    }

    [Fact]
    public async Task Download_refuses_urls_outside_github()
    {
        var c = Make(new FakeHandler());
        var evil = new UpdateInfo(new Version(9, 0), "https://evil.example.com/GpsBridge-setup.exe", "", "", null, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => c.DownloadAsync(evil, Path.Combine(_dir, "x.exe")));
    }
}
