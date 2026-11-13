using System.IO;
using MeslyPdfReader.Convert;
using MeslyPdfReader.Core;
using MeslyPdfReader.Ocr;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.Services;

public enum ConvertFormat { Word, Excel, PowerPoint, Image, Text }

public sealed record ConvertRequest(
    ConvertFormat Format, IReadOnlyList<int> Pages, bool RecognizeScanned, OcrRunOptions Ocr,
    bool DetectTables = true, bool IncludeImages = true, bool OneSheetPerPage = true, bool TablesOnly = false,
    ImageFormatKind ImageFormat = ImageFormatKind.Png, int ImageDpi = 150, int JpegQuality = 90);

/// <summary>Dönüştürme sonucu. Resimde <see cref="Files"/> her sayfanın dosyasıdır; diğerlerinde tek dosya.</summary>
public sealed record ConvertResult(string Path, int Pages, int RecognizedPages, int ScannedNotRecognized, int Tables, int Images, int Paragraphs)
{
    public IReadOnlyList<string> Files { get; init; } = new[] { Path };
    public ConvertFormat Format { get; init; }
}

/// <summary>
/// PDF → Word / Excel / PowerPoint / Resim / Metin. Taranmış (metinsiz) sayfalar istenirse önce belgenin geçici bir kopyasında tanınır;
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

    public static string Extension(ConvertFormat f, ImageFormatKind img = ImageFormatKind.Png) => f switch
    {
        ConvertFormat.Word => ".docx",
        ConvertFormat.Excel => ".xlsx",
        ConvertFormat.PowerPoint => ".pptx",
        ConvertFormat.Text => ".txt",
        _ => img switch { ImageFormatKind.Jpeg => ".jpg", ImageFormatKind.Tiff => ".tif", ImageFormatKind.Bmp => ".bmp", _ => ".png" },
    };

    public static string DisplayName(ConvertFormat f) => f switch
    {
        ConvertFormat.Word => "Word (.docx)",
        ConvertFormat.Excel => "Excel (.xlsx)",
        ConvertFormat.PowerPoint => "PPT (.pptx)",
        ConvertFormat.Image => "Resim",
        _ => "TXT (.txt)",
    };

    public static string TaskKind(ConvertFormat f) => f switch
    {
        ConvertFormat.Word => "convert-docx", ConvertFormat.Excel => "convert-xlsx", ConvertFormat.PowerPoint => "convert-pptx",
        ConvertFormat.Image => "convert-images", _ => "convert-txt",
    };

    /// <summary>Resim dönüştürmede sayfa dosyasının yolu: “ad_s001.png”.</summary>
    public static string ImagePath(string target, int page, int pageCount)
    {
        var dir = Path.GetDirectoryName(target) ?? "";
        var name = Path.GetFileNameWithoutExtension(target);
        int digits = Math.Max(3, pageCount.ToString().Length);
        return Path.Combine(dir, $"{name}_s{(page + 1).ToString().PadLeft(digits, '0')}{Path.GetExtension(target)}");
    }

    public static async Task<ConvertResult> ConvertAsync(PdfDocument doc, string target, ConvertRequest r, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (r.Pages.Count == 0) throw new ArgumentException("Dönüştürülecek sayfa seçilmedi.");
        if (r.Format == ConvertFormat.Image) return await ImagesAsync(doc, target, r, progress, ct);
        var scanned = r.Format == ConvertFormat.Image ? new List<int>() : await ScannedPagesAsync(doc, r.Pages, ct);
        bool ocr = r.RecognizeScanned && scanned.Count > 0;
        if (ocr && OcrService.Unavailable() is { } why) throw new OcrException(why);
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
            if (r.Format == ConvertFormat.Text)
            {
                var t = target + ".mesly-tmp.txt";
                try { await PdfTools.ExportTextAsync(source, t, r.Pages, rest, ct); File.Move(t, target, overwrite: true); }
                finally { if (File.Exists(t)) File.Delete(t); }
                return new ConvertResult(target, r.Pages.Count, recognized, ocr ? 0 : scanned.Count, 0, 0, 0) { Format = ConvertFormat.Text };
            }
            var layouts = await PdfConverter.AnalyzeAsync(source, r.Pages,
                new LayoutAnalyzer.Options(r.DetectTables, r.Format != ConvertFormat.Excel && r.IncludeImages), rest, ct);
            var title = Path.GetFileNameWithoutExtension(target);
            await Task.Run(() =>
            {
                var t = target + ".mesly-tmp" + Path.GetExtension(target);
                try
                {
                    if (r.Format == ConvertFormat.Word) DocxWriter.Write(t, layouts, title);
                    else if (r.Format == ConvertFormat.PowerPoint) PptxWriter.Write(t, layouts, title);
                    else XlsxWriter.Write(t, layouts, new XlsxWriter.Options(r.OneSheetPerPage, r.TablesOnly));
                    File.Move(t, target, overwrite: true);
                }
                finally { if (File.Exists(t)) File.Delete(t); }
            }, ct);
            var blocks = layouts.SelectMany(l => l.Blocks).ToList();
            return new ConvertResult(target, r.Pages.Count, recognized, ocr ? 0 : scanned.Count,
                blocks.OfType<TableBlock>().Count(), blocks.OfType<ImageBlock>().Count(), blocks.OfType<ParagraphBlock>().Count()) { Format = r.Format };
        }
        finally
        {
            work?.Dispose();
            if (tmp is not null) try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>PDF → resim: her sayfa ayrı dosya (TIFF dahil). Var olan dosyaların üzerine yazılır (hedef kullanıcı seçimidir).</summary>
    private static async Task<ConvertResult> ImagesAsync(PdfDocument doc, string target, ConvertRequest r, IProgress<double>? progress, CancellationToken ct)
    {
        var files = new List<string>();
        for (int k = 0; k < r.Pages.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            int page = r.Pages[k];
            var img = await PdfTools.RenderAtDpiAsync(doc, page, r.ImageDpi, ct);
            var path = r.Pages.Count == 1 ? target : ImagePath(target, page, doc.PageCount);
            await Task.Run(() => ImageCodec.Save(img, path, r.ImageFormat, r.ImageDpi, r.JpegQuality), ct);
            files.Add(path);
            progress?.Report((k + 1.0) / r.Pages.Count);
        }
        return new ConvertResult(files[0], r.Pages.Count, 0, 0, 0, files.Count, 0) { Files = files, Format = ConvertFormat.Image };
    }

    public static string Summarize(ConvertResult r)
    {
        if (r.Format == ConvertFormat.Image)
            return $"{r.Pages} sayfa {r.Files.Count} resim dosyasına dönüştürüldü.";
        if (r.Format == ConvertFormat.Text)
        {
            var t = $"{r.Pages} sayfanın metni kaydedildi.";
            if (r.RecognizedPages > 0) t += $" Taranmış {r.RecognizedPages} sayfanın metni tanındı (özgün belge değişmedi).";
            if (r.ScannedNotRecognized > 0) t += $" {r.ScannedNotRecognized} taranmış sayfanın metni yok (tanıma kapalı).";
            return t;
        }
        var parts = new List<string> { $"{r.Pages} sayfa", $"{r.Paragraphs} paragraf" };
        if (r.Tables > 0) parts.Add($"{r.Tables} tablo");
        if (r.Images > 0) parts.Add($"{r.Images} resim");
        var s = string.Join(", ", parts) + " dönüştürüldü.";
        if (r.RecognizedPages > 0) s += $" Taranmış {r.RecognizedPages} sayfanın metni tanındı (özgün belge değişmedi).";
        if (r.ScannedNotRecognized > 0) s += $" {r.ScannedNotRecognized} taranmış sayfa tanınmadan aktarıldı; bu sayfaların metni çıkmaz.";
        return s;
    }
}
