using GpsBridge.Core.Map;
using Xunit;

namespace GpsBridge.Core.Tests;

public class TileCacheTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "gpsb-tile-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Second_request_hits_the_cache()
    {
        int fetches = 0;
        var c = new TileCache(_dir, 1_000_000, url => { fetches++; return Task.FromResult(new byte[] { 1, 2, 3 }); });
        Assert.Equal(new byte[] { 1, 2, 3 }, await c.GetAsync(5, 10, 12));
        Assert.Equal(new byte[] { 1, 2, 3 }, await c.GetAsync(5, 10, 12));
        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task Fetch_failure_without_cache_returns_null()
    {
        var c = new TileCache(_dir, 1_000_000, _ => throw new HttpRequestException("offline"));
        Assert.Null(await c.GetAsync(1, 1, 1));
    }

    [Fact]
    public async Task Oldest_tiles_are_evicted_over_the_limit()
    {
        var c = new TileCache(_dir, 250, _ => Task.FromResult(new byte[100]), checkEvery: 1);
        await c.GetAsync(1, 0, 0); await Task.Delay(30);
        await c.GetAsync(1, 0, 1); await Task.Delay(30);
        await c.GetAsync(1, 1, 0);
        Assert.False(File.Exists(Path.Combine(_dir, "1", "0", "0.png")));
        Assert.True(File.Exists(Path.Combine(_dir, "1", "1", "0.png")));
    }

    [Fact]
    public void Url_uses_osm_scheme()
    {
        Assert.Equal("https://tile.openstreetmap.org/5/10/12.png", TileCache.OsmUrl(5, 10, 12));
    }
}
