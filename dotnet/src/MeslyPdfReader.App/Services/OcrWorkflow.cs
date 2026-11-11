using System.IO;
using MeslyPdfReader.Core;
using MeslyPdfReader.Data;
using MeslyPdfReader.Ocr;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.Services;

public sealed record OcrRunOptions(string Languages, ImageCorrection Correction, int Dpi = 300, bool SkipPagesWithText = true)
{
    public static OcrRunOptions From(AppSettings s) =>
        new(s.OcrLanguages, new ImageCorrection(s.OcrAutoRotate, s.OcrDeskew, s.OcrCleanBorders, s.OcrWhiten), s.OcrDpi);
}

/// <summary>Bir sayfanın tanıma sonucu.</summary>
public sealed record OcrPageResult(int Page, OcrPage? Ocr, CorrectionReport? Correction, string? Skipped)
{
    public int Words => Ocr?.Words.Count ?? 0;
    public int LowConfidence(float limit = 60) => Ocr?.Words.Count(w => w.Confidence < limit) ?? 0;
}

/// <summary>
/// Belge sayfalarını düzeltip tanır: yön (kayıpsız sayfa döndürme), eğiklik ve kenar temizleme (taranmış görüntü
/// yeniden yazılır), ardından OCR ve görünmez metin katmanı. Her şey bu bilgisayarda yapılır.
/// </summary>
public static class OcrWorkflow
{
    public static byte[] LayerFont => FontResolver.Instance.Load(FontResolver.BundledPath);

    public static void Init()
    {
        PdfDocument.JpegEncoder = img => ImageCodec.EncodeJpeg(img, 90);
        PdfDocument.JpegEncoderWithQuality = (img, q, gray) => ImageCodec.EncodeJpeg(img, q, 150, gray);
        // Word'e dönüşümde fotoğraflar JPEG (küçük dosya), çizimler/şemalar PNG (keskin) yazılır.
        Convert.DocxWriter.ImageEncoder = (img, preferJpeg) => preferJpeg
            ? (ImageCodec.EncodeJpeg(img, 88, 150), "image/jpeg")
            : (ImageCodec.EncodePng(img, 150), "image/png");
    }

    public static async Task<IReadOnlyList<OcrPageResult>> RecognizeAsync(PdfDocument doc, IReadOnlyList<int> pages, OcrRunOptions o,
        IProgress<double>? progress = null, CancellationToken ct = default, bool ocr = true)
    {
        if (ocr && OcrEngine.Unavailable() is { } why) throw new OcrException(why);
        var results = new OcrPageResult[pages.Count];
        int done = 0;
        double[] partial = new double[pages.Count];
        void Report() => progress?.Report((done + partial.Sum()) / Math.Max(1, pages.Count));
        using var gate = new SemaphoreSlim(OcrService.MaxParallel);
        var tasks = pages.Select(async (page, k) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                results[k] = await OnePageAsync(doc, page, o, ocr, p => { partial[k] = p; Report(); }, ct);
            }
            finally
            {
                partial[k] = 0; Interlocked.Increment(ref done); Report();
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);
        return results;
    }

    private static async Task<OcrPageResult> OnePageAsync(PdfDocument doc, int page, OcrRunOptions o, bool ocr, Action<double> progress, CancellationToken ct)
    {
        if (ocr && o.SkipPagesWithText && await doc.CountRealTextCharsAsync(page) > 40)
            return new OcrPageResult(page, null, null, "sayfada zaten seçilebilir metin var");
        int turns = 0; double skew = 0; bool borders = false, whiten = false;
        var c = o.Correction;
        // 1) Yön: sayfa döndürmesi değiştirilir (kayıpsız).
        if (c.AutoRotate)
        {
            var low = await PdfTools.RenderAtDpiAsync(doc, page, 150, ct);
            turns = await Task.Run(() => OcrService.DetectQuarterTurns(low, 150, o.Languages, ct), ct);
            if (turns != 0) await doc.RotatePagesAsync(new[] { page }, turns);
        }
        progress(0.15);
        // 2) Eğiklik / kenar / arka plan: yalnızca sayfanın tamamını kaplayan taranmış görüntü üzerinde.
        if (c.Deskew || c.CleanBorders || c.WhitenBackground)
        {
            var main = await doc.GetMainImageAsync(page);
            if (main is { CoverRatio: >= 0.85 })
            {
                var px = await doc.GetImagePixelsAsync(page, main.ObjectIndex);
                int imgDpi = (int)Math.Round(main.PixelWidth / Math.Max(1, main.Bounds.Width) * 72);
                var (fixedImg, rep) = await Task.Run(() => OcrService.Correct(px, imgDpi, c with { AutoRotate = false }, o.Languages, ct), ct);
                if (rep.Changed)
                {
                    await doc.SetImagePixelsAsync(page, main.ObjectIndex, fixedImg);
                    skew = rep.DeskewDegrees; borders = rep.BordersCleaned; whiten = rep.BackgroundWhitened;
                }
            }
        }
        var report = new CorrectionReport(turns, skew, borders, whiten);
        progress(0.25);
        if (!ocr) return new OcrPageResult(page, null, report, null);
        // 3) Tanıma ve görünmez metin katmanı.
        var img = await PdfTools.RenderAtDpiAsync(doc, page, o.Dpi, ct);
        var result = await OcrService.RecognizeAsync(img, o.Dpi, o.Languages, ct, p => progress(0.25 + p * 0.7));
        await doc.AddOcrLayerAsync(page, result, LayerFont);
        return new OcrPageResult(page, result, report, null);
    }

    /// <summary>Taranan tek bir görüntüyü (tarama penceresinde) düzeltir. Değişmediyse aynı veri döner.</summary>
    public static (ScannedPage Page, CorrectionReport Report) CorrectScan(ScannedPage p, ImageCorrection c, string languages, CancellationToken ct = default)
    {
        var img = ImageCodec.Decode(p.Data);
        var (fixedImg, rep) = OcrService.Correct(img, p.Dpi, c, languages, ct);
        if (!rep.Changed) return (p, rep);
        var data = p.Format == ScannedFormat.Jpeg ? ImageCodec.EncodeJpeg(fixedImg, 92, p.Dpi) : ImageCodec.EncodePng(fixedImg, p.Dpi);
        return (new ScannedPage { Data = data, Format = p.Format == ScannedFormat.Jpeg ? ScannedFormat.Jpeg : ScannedFormat.Png, Dpi = p.Dpi, QuarterTurns = p.QuarterTurns }, rep);
    }

    public static string Summarize(IReadOnlyList<OcrPageResult> r)
    {
        int recognized = r.Count(x => x.Ocr is not null), skipped = r.Count(x => x.Skipped is not null);
        int rotated = r.Count(x => x.Correction?.QuarterTurnsApplied is > 0), deskewed = r.Count(x => Math.Abs(x.Correction?.DeskewDegrees ?? 0) > 0.01);
        int words = r.Sum(x => x.Words), low = r.Sum(x => x.LowConfidence());
        var parts = new List<string>();
        if (recognized > 0) parts.Add($"{recognized} sayfa tanındı ({words} sözcük)");
        if (rotated > 0) parts.Add($"{rotated} sayfanın yönü düzeltildi");
        if (deskewed > 0) parts.Add($"{deskewed} sayfanın eğikliği düzeltildi");
        if (skipped > 0) parts.Add($"{skipped} sayfada zaten metin vardı, atlandı");
        if (low > 0) parts.Add($"{low} sözcük düşük güvenli — “Doğrula” ile kontrol edin");
        return parts.Count == 0 ? "Değişiklik yapılmadı." : string.Join(" · ", parts) + ".";
    }
}
