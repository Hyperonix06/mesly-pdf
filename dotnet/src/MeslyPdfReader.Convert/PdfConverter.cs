using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.Convert;

/// <summary>PDF → Word / Excel dönüştürme. Taranmış sayfalar önce OCR'dan geçirilmiş olmalıdır (metin katmanı).</summary>
public static class PdfConverter
{
    public static async Task<IReadOnlyList<PageLayout>> AnalyzeAsync(PdfDocument doc, IReadOnlyList<int> pages, LayoutAnalyzer.Options? o = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var list = new List<PageLayout>(pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            list.Add(await LayoutAnalyzer.AnalyzeAsync(doc, pages[i], o, ct));
            progress?.Report((i + 1.0) / pages.Count);
        }
        return list;
    }

    public static async Task ToDocxAsync(PdfDocument doc, string path, IReadOnlyList<int> pages, string? title = null,
        LayoutAnalyzer.Options? layout = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var layouts = await AnalyzeAsync(doc, pages, layout, progress, ct);
        await Task.Run(() => WriteAtomic(path, tmp => DocxWriter.Write(tmp, layouts, title)), ct);
    }

    public static async Task ToXlsxAsync(PdfDocument doc, string path, IReadOnlyList<int> pages, XlsxWriter.Options? o = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var layouts = await AnalyzeAsync(doc, pages, new LayoutAnalyzer.Options(IncludeImages: false), progress, ct);
        await Task.Run(() => WriteAtomic(path, tmp => XlsxWriter.Write(tmp, layouts, o)), ct);
    }

    /// <summary>Geçici dosyaya yazıp tamamlanınca yerine taşır: yarım kalan dönüşüm mevcut dosyayı bozmaz.</summary>
    private static void WriteAtomic(string path, Action<string> write)
    {
        var tmp = path + ".mesly-tmp" + Path.GetExtension(path);
        try
        {
            write(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
