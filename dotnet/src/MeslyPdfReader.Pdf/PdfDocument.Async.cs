using MeslyPdfReader.Core;

namespace MeslyPdfReader.Pdf;

// async yöntemler unsafe bağlamda olamaz; bu kısım "unsafe" değildir.
public sealed partial class PdfDocument
{
    /// <summary>Belgedeki tüm metin (sayfalar arasında boş satır).</summary>
    public async Task<string> ExtractAllTextAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        int count = PageCount;
        for (int i = 0; i < count; i++)
        {
            var t = await GetPageTextAsync(i, ct, lowPriority: true).ConfigureAwait(false);
            if (i > 0) sb.Append("\r\n\r\n");
            sb.Append(NormalizeNewlines(t.Text).TrimEnd());
            progress?.Report((i + 1) / (double)count);
        }
        return sb.ToString();
    }

    /// <summary>Türkçe kurallarıyla tüm belgede arar. Sonuçlar sayfa sayfa bildirilir.</summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, SearchOptions options,
        IProgress<(int Page, IReadOnlyList<SearchHit> Hits)>? progress = null, CancellationToken ct = default, int startPage = 0)
    {
        var all = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return all;
        int count = PageCount;
        for (int k = 0; k < count; k++)
        {
            ct.ThrowIfCancellationRequested();
            int i = (startPage + k) % count;
            var hits = await _engine.RunAsync(() =>
            {
                var t = BuildPageText(i);
                var list = new List<SearchHit>();
                foreach (var (idx, len) in TextSearch.FindAll(t.Text, query, options))
                    list.Add(new SearchHit(i, idx, len, t.GetLineRects(idx, idx + len - 1), TextSearch.Context(t.Text, idx, len)));
                return (IReadOnlyList<SearchHit>)list;
            }, ct, lowPriority: true).ConfigureAwait(false);
            all.AddRange(hits);
            progress?.Report((i, hits));
        }
        all.Sort((a, b) => a.PageIndex != b.PageIndex ? a.PageIndex.CompareTo(b.PageIndex) : a.CharIndex.CompareTo(b.CharIndex));
        return all;
    }

    /// <summary>Bir dosyadan sayfa ekler.</summary>
    public async Task InsertFromFileAsync(string path, string? password, IEnumerable<int>? indices, int at)
    {
        using var src = await OpenAsync(path, password).ConfigureAwait(false);
        await ImportPagesAsync(src, indices, at).ConfigureAwait(false);
    }

    /// <summary>Görüntülerden yeni bir PDF oluşturur (her görüntü bir sayfa).</summary>
    public static async Task<PdfDocument> CreateFromImagesAsync(IReadOnlyList<ImagePageSource> images, ImagePageLayout layout,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (images.Count == 0) throw new ArgumentException("Hiç görüntü seçilmedi.");
        var doc = await CreateAsync().ConfigureAwait(false);
        try
        {
            for (int i = 0; i < images.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await doc.AppendImagePageAsync(images[i], layout).ConfigureAwait(false);
                progress?.Report((i + 1) / (double)images.Count);
            }
            return doc;
        }
        catch { doc.Dispose(); throw; }
    }
}
