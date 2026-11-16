namespace GpsBridge.Core.Map;

/// <summary>Cache su disco delle tile OSM (z/x/y.png): serve la mappa anche senza rete. Nessun prescaricamento (policy OSM).</summary>
public sealed class TileCache
{
    readonly string _dir;
    readonly long _maxBytes;
    readonly Func<string, Task<byte[]>> _fetch;
    readonly int _checkEvery;
    readonly SemaphoreSlim _evictLock = new(1, 1);
    int _writes;

    public TileCache(string dir, long maxBytes, Func<string, Task<byte[]>> fetch, int checkEvery = 50)
    {
        _dir = dir; _maxBytes = maxBytes; _fetch = fetch; _checkEvery = checkEvery;
    }

    public static string OsmUrl(int z, int x, int y) => $"https://tile.openstreetmap.org/{z}/{x}/{y}.png";

    string PathFor(int z, int x, int y) => Path.Combine(_dir, z.ToString(), x.ToString(), y + ".png");

    public async Task<byte[]?> GetAsync(int z, int x, int y)
    {
        var path = PathFor(z, x, y);
        try { if (File.Exists(path)) return await File.ReadAllBytesAsync(path); } catch { /* tile corrotta/in uso: si riscarica */ }
        byte[] data;
        try { data = await _fetch(OsmUrl(z, x, y)); }
        catch { return null; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, data);
            File.Move(tmp, path, true);
            if (Interlocked.Increment(ref _writes) % _checkEvery == 0) await Evict();
        }
        catch { /* disco pieno o permessi: la tile si serve comunque */ }
        return data;
    }

    async Task Evict()
    {
        await _evictLock.WaitAsync();
        try
        {
            var files = new DirectoryInfo(_dir).EnumerateFiles("*.png", SearchOption.AllDirectories)
                            .OrderBy(f => f.LastWriteTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= _maxBytes) break;
                total -= f.Length;
                try { f.Delete(); } catch { }
            }
        }
        finally { _evictLock.Release(); }
    }

    public void Clear() { try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { } }
}
