using System.IO;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.ViewModels;

/// <summary>
/// Ekranların (Dönüştür, OCR, Sayfa Yönetimi) üzerinde çalıştığı PDF. Dosya bir sekmede açıksa o belge kullanılır
/// (kaydedilmemiş düzenlemeler dahil); değilse dosya ayrıca açılır ve kaynak değişince kapatılır. Özgün dosya değişmez.
/// </summary>
public sealed class SourcePdf : IDisposable
{
    private readonly bool _owned;

    private SourcePdf(string path, PdfDocument doc, bool owned, DocumentViewModel? tab)
    {
        Path = path; Document = doc; _owned = owned; Tab = tab;
    }

    public string Path { get; }
    public PdfDocument Document { get; }
    /// <summary>Kaynak açık bir sekmeyse o sekme.</summary>
    public DocumentViewModel? Tab { get; }
    public string Name => Tab?.Title ?? System.IO.Path.GetFileName(Path);
    public int PageCount => Document.PageCount;
    public long Size => File.Exists(Path) ? new FileInfo(Path).Length : 0;
    public string Info => $"{(Size > 0 ? RecentItem.FormatSize(Size) + " • " : "")}{PageCount} sayfa{(Tab?.Document.IsModified == true ? " • kaydedilmemiş değişiklikler dahil" : "")}";

    /// <summary>Dosyayı (gerekirse parola sorarak) açar. Vazgeçilirse null.</summary>
    public static async Task<SourcePdf?> OpenAsync(MainViewModel main, string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var tab = main.Tabs.FirstOrDefault(t => t.Document.FilePath is { } fp && string.Equals(fp, full, StringComparison.OrdinalIgnoreCase));
        if (tab is not null) return new SourcePdf(full, tab.Document, false, tab);
        var doc = await main.OpenWithPasswordAsync(full);
        return doc is null ? null : new SourcePdf(full, doc, true, null);
    }

    public static SourcePdf FromTab(DocumentViewModel tab) => new(tab.Document.FilePath ?? tab.Title, tab.Document, false, tab);

    /// <summary>Bellekte oluşturulmuş belge (görüntülerden, tarayıcıdan); sahiplenilir.</summary>
    public static SourcePdf FromDocument(PdfDocument doc, string name) => new(name, doc, true, null);

    public void Dispose() { if (_owned) Document.Dispose(); }
}
