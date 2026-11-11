using System.IO.Compression;
using MeslyPdfReader.Core;

namespace MeslyPdfReader.Pdf;

public sealed record MergeSource(string Path, string? Password = null, string? PageRange = null);

public enum SplitMode { EveryNPages, ByRanges, SinglePages, OddEven }

public sealed record SplitPlan(SplitMode Mode, int PagesPerFile = 1, string? Ranges = null);

/// <summary>Belge düzeyi araçlar: birleştirme, bölme, görüntü/metin dışa aktarma.</summary>
public static class PdfTools
{
    /// <summary>Birden çok PDF'i sırayla tek belgede birleştirir (kaydedilmemiş yeni belge döner).</summary>
    public static async Task<PdfDocument> MergeAsync(IReadOnlyList<MergeSource> sources, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (sources.Count == 0) throw new ArgumentException("Birleştirilecek dosya seçilmedi.");
        var result = await PdfDocument.CreateAsync().ConfigureAwait(false);
        try
        {
            for (int i = 0; i < sources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = sources[i];
                PdfDocument src;
                try { src = await PdfDocument.OpenAsync(s.Path, s.Password, ct).ConfigureAwait(false); }
                catch (PdfException ex) { throw new PdfException(ex.Kind, $"({Path.GetFileName(s.Path)})", ex); }
                using (src)
                {
                    var pages = Core.PageRange.Parse(s.PageRange, src.PageCount);
                    await result.ImportPagesAsync(src, pages, result.PageCount).ConfigureAwait(false);
                }
                progress?.Report((i + 1) / (double)sources.Count);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>Bölme planını sayfa gruplarına çevirir (0 tabanlı).</summary>
    public static IReadOnlyList<IReadOnlyList<int>> PlanSplit(int pageCount, SplitPlan plan)
    {
        var groups = new List<IReadOnlyList<int>>();
        switch (plan.Mode)
        {
            case SplitMode.EveryNPages:
                int n = Math.Max(1, plan.PagesPerFile);
                for (int i = 0; i < pageCount; i += n) groups.Add(Enumerable.Range(i, Math.Min(n, pageCount - i)).ToArray());
                break;
            case SplitMode.SinglePages:
                for (int i = 0; i < pageCount; i++) groups.Add(new[] { i });
                break;
            case SplitMode.OddEven:
                groups.Add(Enumerable.Range(0, pageCount).Where(i => i % 2 == 0).ToArray());
                if (pageCount > 1) groups.Add(Enumerable.Range(0, pageCount).Where(i => i % 2 == 1).ToArray());
                break;
            case SplitMode.ByRanges:
                var parts = (plan.Ranges ?? "").Split(new[] { ';', '|', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0) throw new FormatException("En az bir sayfa aralığı yazın. Örnek: 1-3; 4-10; 11-");
                foreach (var p in parts) groups.Add(Core.PageRange.Parse(p, pageCount));
                break;
        }
        return groups;
    }

    /// <summary>Belgeyi gruplara göre ayrı dosyalara böler. Oluşan dosya yollarını döndürür.</summary>
    public static async Task<IReadOnlyList<string>> SplitAsync(PdfDocument doc, IReadOnlyList<IReadOnlyList<int>> groups,
        string outputDirectory, string baseName, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var files = new List<string>();
        int digits = Math.Max(2, groups.Count.ToString().Length);
        for (int g = 0; g < groups.Count; g++)
        {
            ct.ThrowIfCancellationRequested();
            var pages = groups[g];
            var name = $"{baseName}_{(g + 1).ToString().PadLeft(digits, '0')} (s. {Core.PageRange.Format(pages)}).pdf";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var path = UniquePath(Path.Combine(outputDirectory, name));
            using var part = await doc.ExtractAsync(pages).ConfigureAwait(false);
            await part.SaveAsync(path, ct).ConfigureAwait(false);
            files.Add(path);
            progress?.Report((g + 1) / (double)groups.Count);
        }
        return files;
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!; var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    /// <summary>Sayfayı verilen DPI'da çizer (sayfa sınırları 20.000 pikselle sınırlıdır).</summary>
    public static Task<BgraImage> RenderAtDpiAsync(PdfDocument doc, int page, double dpi, CancellationToken ct = default, bool printing = false)
    {
        var size = doc.GetPageSize(page);
        double scale = dpi / 72.0;
        int w = (int)Math.Round(size.Width * scale), h = (int)Math.Round(size.Height * scale);
        double limit = 20000.0 / Math.Max(w, h);
        if (limit < 1) { w = (int)(w * limit); h = (int)(h * limit); }
        return doc.RenderAsync(page, new RenderRequest(Math.Max(1, w), Math.Max(1, h), Printing: printing), ct, lowPriority: true);
    }

    /// <summary>Belgenin metnini UTF-8 (BOM'lu, Not Defteri uyumlu) TXT dosyasına yazar.</summary>
    public static async Task ExportTextAsync(PdfDocument doc, string path, IReadOnlyList<int>? pages = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var list = pages ?? Enumerable.Range(0, doc.PageCount).ToArray();
        await using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        for (int i = 0; i < list.Count; i++)
        {
            var t = await doc.GetPageTextAsync(list[i], ct, lowPriority: true).ConfigureAwait(false);
            if (i > 0) await w.WriteAsync("\r\n\r\n").ConfigureAwait(false);
            var text = t.Text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n").TrimEnd();
            await w.WriteAsync(text).ConfigureAwait(false);
            progress?.Report((i + 1) / (double)list.Count);
        }
    }
}

/// <summary>Bağımlılıksız PNG kodlayıcı (BGRA → RGBA 8 bit, zlib). Platformdan bağımsız dışa aktarım ve testler için.</summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static void Write(BgraImage img, Stream output, double dpi = 96, bool opaque = true)
    {
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)img.Width); WriteBE(ihdr, 4, (uint)img.Height);
        ihdr[8] = 8; ihdr[9] = (byte)(opaque ? 2 : 6); ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(output, "IHDR", ihdr);
        uint ppm = (uint)Math.Round(dpi / 0.0254);
        var phys = new byte[9]; WriteBE(phys, 0, ppm); WriteBE(phys, 4, ppm); phys[8] = 1;
        Chunk(output, "pHYs", phys);

        int bpp = opaque ? 3 : 4;
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + img.Width * bpp];
            for (int y = 0; y < img.Height; y++)
            {
                row[0] = 0;
                int si = y * img.Stride, di = 1;
                for (int x = 0; x < img.Width; x++, si += 4)
                {
                    row[di++] = img.Pixels[si + 2]; row[di++] = img.Pixels[si + 1]; row[di++] = img.Pixels[si];
                    if (!opaque) row[di++] = img.Pixels[si + 3];
                }
                z.Write(row);
            }
        }
        Chunk(output, "IDAT", raw.ToArray());
        Chunk(output, "IEND", Array.Empty<byte>());
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, (uint)data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t); s.Write(data);
        uint c = 0xFFFFFFFF;
        foreach (var b in t) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        var crc = new byte[4]; WriteBE(crc, 0, c ^ 0xFFFFFFFF); s.Write(crc);
    }

    private static void WriteBE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
}
