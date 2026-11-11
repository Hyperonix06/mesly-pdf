using System.Windows;
using System.Windows.Media.Imaging;

namespace MeslyPdfReader.App.Controls;

internal readonly record struct RenderKey(int Page, int Revision, int Width, int Height, int Turns, bool Dark, int RX, int RY, int RW, int RH)
{
    public bool IsRegion => RW > 0;
}

internal sealed class CacheEntry
{
    public required RenderKey Key { get; init; }
    public required BitmapSource Bitmap { get; init; }
    public long Bytes => (long)Bitmap.PixelWidth * Bitmap.PixelHeight * 4;
    public LinkedListNode<CacheEntry>? Node { get; set; }
}

/// <summary>
/// Sayfa görüntüleri için bayt bütçeli LRU önbellek. Her sayfa için bir tam görüntü ve (yüksek yakınlaştırmada)
/// bir bölge görüntüsü tutulur. Bütçe aşılınca en uzun süre kullanılmayanlar atılır.
/// </summary>
internal sealed class RenderCache
{
    private readonly Dictionary<int, CacheEntry> _full = new();
    private readonly Dictionary<int, CacheEntry> _region = new();
    private readonly LinkedList<CacheEntry> _lru = new();
    public long Budget { get; set; }
    public long Bytes { get; private set; }
    public int Count => _lru.Count;

    public RenderCache(long budgetBytes) => Budget = budgetBytes;

    public CacheEntry? GetFull(int page) => Touch(_full.GetValueOrDefault(page));
    public CacheEntry? GetRegion(int page) => Touch(_region.GetValueOrDefault(page));

    private CacheEntry? Touch(CacheEntry? e)
    {
        if (e?.Node is { } n) { _lru.Remove(n); _lru.AddFirst(n); }
        return e;
    }

    public void Put(CacheEntry e)
    {
        var map = e.Key.IsRegion ? _region : _full;
        if (map.TryGetValue(e.Key.Page, out var old)) Remove(old, map);
        map[e.Key.Page] = e;
        e.Node = _lru.AddFirst(e);
        Bytes += e.Bytes;
        Trim();
    }

    private void Remove(CacheEntry e, Dictionary<int, CacheEntry> map)
    {
        if (e.Node is not null) { _lru.Remove(e.Node); e.Node = null; }
        map.Remove(e.Key.Page);
        Bytes -= e.Bytes;
    }

    /// <summary>Bütçeyi aşan girdileri atar; görünen sayfaların görüntüleri korunur.</summary>
    public void Trim(ISet<int>? keep = null)
    {
        var node = _lru.Last;
        while (Bytes > Budget && node is not null)
        {
            var prev = node.Previous;
            var e = node.Value;
            if (keep is null || !keep.Contains(e.Key.Page))
                Remove(e, e.Key.IsRegion ? _region : _full);
            node = prev;
        }
    }

    public void DropRegionsExcept(ISet<int> pages)
    {
        foreach (var e in _region.Values.ToList())
            if (!pages.Contains(e.Key.Page)) Remove(e, _region);
    }

    public void Clear()
    {
        _full.Clear(); _region.Clear(); _lru.Clear(); Bytes = 0;
    }
}
