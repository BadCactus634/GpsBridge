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

public class TileLayerTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "gpsb-tl-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task Layers_use_their_own_url_and_folder_and_unknown_layer_is_refused()
    {
        var urls = new List<string>();
        var c = new TileCache(_dir, 1_000_000, u => { urls.Add(u); return Task.FromResult(new byte[] { 9 }); });
        await c.GetAsync("sat", 3, 4, 5); await c.GetAsync("topo", 3, 4, 5);
        Assert.Contains("World_Imagery/MapServer/tile/3/5/4", urls[0]);
        Assert.Equal("https://tile.opentopomap.org/3/4/5.png", urls[1]);
        Assert.True(File.Exists(Path.Combine(_dir, "sat", "3", "4", "5.png")));
        Assert.Null(await c.GetAsync("../x", 1, 1, 1));
    }
}
