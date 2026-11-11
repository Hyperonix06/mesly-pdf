using System.IO;
using MeslyPdfReader.Convert;
using MeslyPdfReader.Core;
using MeslyPdfReader.Ocr;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.Services;

public enum ConvertFormat { Word, Excel }

public sealed record ConvertRequest(
    ConvertFormat Format, IReadOnlyList<int> Pages, bool RecognizeScanned, OcrRunOptions Ocr,
    bool DetectTables = true, bool IncludeImages = true, bool OneSheetPerPage = true, bool TablesOnly = false);

public sealed record ConvertResult(string Path, int Pages, int RecognizedPages, int ScannedNotRecognized, int Tables, int Images, int Paragraphs);

/// <summary>
/// PDF → Word / Excel. Taranmış (metinsiz) sayfalar istenirse önce belgenin geçici bir kopyasında tanınır;
/// açık belge ve diskteki özgün dosya hiç değişmez. Her şey bu bilgisayarda yapılır.
/// </summary>
public static class ConvertWorkflow
{
    /// <summary>Metin katmanı olmayan, görüntüden oluşan sayfalar.</summary>
    public static async Task<List<int>> ScannedPagesAsync(PdfDocument doc, IReadOnlyList<int> pages, CancellationToken ct = default)
    {
        var list = new List<int>();
        foreach (var p in pages)
        {
            ct.ThrowIfCancellationRequested();
            if (await doc.CountRealTextCharsAsync(p) <= 40 && !await doc.HasOcrLayerAsync(p) && await doc.GetMainImageAsync(p) is not null) list.Add(p);
        }
        return list;
    }

    public static async Task<ConvertResult> ConvertAsync(PdfDocument doc, string target, ConvertRequest r, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var scanned = await ScannedPagesAsync(doc, r.Pages, ct);
        bool ocr = r.RecognizeScanned && scanned.Count > 0;
        if (ocr && OcrEngine.Unavailable() is { } why) throw new OcrException(why);
        string? tmp = null;
        PdfDocument? work = null;
        try
        {
            var source = doc;
            int recognized = 0;
            if (ocr)
            {
                // Geçici kopya: kaydedilmemiş düzenlemeler de dahil, görünen belge dönüştürülür.
                tmp = Path.Combine(AppPaths.Temp, $"convert-{Guid.NewGuid():N}.pdf");
                Directory.CreateDirectory(AppPaths.Temp);
                await doc.SaveCopyAsync(tmp);
                work = await PdfDocument.OpenAsync(tmp, doc.Password, ct);
                var res = await OcrWorkflow.RecognizeAsync(work, scanned, r.Ocr with { SkipPagesWithText = true },
                    new Progress<double>(p => progress?.Report(p * 0.6)), ct);
                recognized = res.Count(x => x.Ocr is not null);
                source = work;
            }
            var rest = new Progress<double>(p => progress?.Report((ocr ? 0.6 : 0) + p * (ocr ? 0.4 : 1)));
            var layouts = await PdfConverter.AnalyzeAsync(source, r.Pages,
                new LayoutAnalyzer.Options(r.DetectTables, r.Format == ConvertFormat.Word && r.IncludeImages), rest, ct);
            var title = Path.GetFileNameWithoutExtension(target);
            await Task.Run(() =>
            {
                var t = target + ".mesly-tmp" + Path.GetExtension(target);
                try
                {
                    if (r.Format == ConvertFormat.Word) DocxWriter.Write(t, layouts, title);
                    else XlsxWriter.Write(t, layouts, new XlsxWriter.Options(r.OneSheetPerPage, r.TablesOnly));
                    File.Move(t, target, overwrite: true);
                }
                finally { if (File.Exists(t)) File.Delete(t); }
            }, ct);
            var blocks = layouts.SelectMany(l => l.Blocks).ToList();
            return new ConvertResult(target, r.Pages.Count, recognized, ocr ? 0 : scanned.Count,
                blocks.OfType<TableBlock>().Count(), blocks.OfType<ImageBlock>().Count(), blocks.OfType<ParagraphBlock>().Count());
        }
        finally
        {
            work?.Dispose();
            if (tmp is not null) try { File.Delete(tmp); } catch { }
        }
    }

    public static string Summarize(ConvertResult r)
    {
        var parts = new List<string> { $"{r.Pages} sayfa", $"{r.Paragraphs} paragraf" };
        if (r.Tables > 0) parts.Add($"{r.Tables} tablo");
        if (r.Images > 0) parts.Add($"{r.Images} resim");
        var s = string.Join(", ", parts) + " dönüştürüldü.";
        if (r.RecognizedPages > 0) s += $" Taranmış {r.RecognizedPages} sayfanın metni tanındı (özgün belge değişmedi).";
        if (r.ScannedNotRecognized > 0) s += $" {r.ScannedNotRecognized} taranmış sayfa tanınmadan aktarıldı; bu sayfaların metni çıkmaz.";
        return s;
    }
}
