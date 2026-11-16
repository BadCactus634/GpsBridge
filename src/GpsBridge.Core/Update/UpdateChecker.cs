using System.Security.Cryptography;
using System.Text.Json;

namespace GpsBridge.Core.Update;

public sealed record UpdateInfo(Version Version, string DownloadUrl, string Notes, string PageUrl, string? Sha256, long Size);

/// <summary>Controlla l'ultima release pubblicata su GitHub e scarica l'installer (con verifica SHA-256 quando GitHub la fornisce).</summary>
public sealed class UpdateChecker
{
    public const string AssetName = "GpsBridge-setup.exe";
    readonly HttpClient _http;
    readonly string _repo, _apiBase;
    public UpdateChecker(HttpClient http, string repo, string apiBase = "https://api.github.com")
    {
        _http = http; _repo = repo; _apiBase = apiBase.TrimEnd('/');
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0) _http.DefaultRequestHeaders.UserAgent.ParseAdd("GpsBridge-updater");
        if (!_http.DefaultRequestHeaders.Accept.Any()) _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>La release piu' recente se e' piu' nuova di <paramref name="current"/>, altrimenti null. Non lancia mai: offline o risposta strana = null.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{_apiBase}/repos/{_repo}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            if (r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
            if (r.TryGetProperty("draft", out var dr) && dr.GetBoolean()) return null;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v) || v <= Normalize(current)) return null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                if (a.GetProperty("name").GetString() != AssetName) continue;
                string? sha = null;
                if (a.TryGetProperty("digest", out var d) && d.GetString() is { } ds && ds.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = ds[7..].ToLowerInvariant();
                return new UpdateInfo(v, a.GetProperty("browser_download_url").GetString()!,
                    r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                    r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                    sha, a.TryGetProperty("size", out var s) ? s.GetInt64() : 0);
            }
            return null;
        }
        catch { return null; }
    }

    static Version Normalize(Version v) => new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>Scarica l'installer in <paramref name="path"/>. Solo da github.com; se manca o non torna lo SHA-256 il file viene cancellato.</summary>
    public async Task DownloadAsync(UpdateInfo u, string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var uri = new Uri(u.DownloadUrl);
        bool trusted = uri.Host == "github.com" || uri.Host.EndsWith(".github.com") || uri.Host.EndsWith(".githubusercontent.com") || uri.Host == new Uri(_apiBase).Host;
        if (!trusted || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
            throw new InvalidOperationException("Indirizzo di download non attendibile: " + uri.Host);
        string tmp = path + ".part";
        try
        {
            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? u.Size;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[81920]; long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct); done += n;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            if (u.Sha256 is { } want)
            {
                await using var fs = File.OpenRead(tmp);
                var got = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
                if (got != want.ToLowerInvariant()) throw new InvalidDataException("Il file scaricato non corrisponde alla firma SHA-256 della release.");
            }
            File.Move(tmp, path, true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }
}
