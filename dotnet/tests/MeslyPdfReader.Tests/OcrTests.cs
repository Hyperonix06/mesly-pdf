using MeslyPdfReader.Core;
using MeslyPdfReader.Ocr;
using MeslyPdfReader.Pdf;
using Xunit;
using Xunit.Abstractions;

namespace MeslyPdfReader.Tests;

/// <summary>Aşama 4: gerçek Tesseract ile OCR, yön/eğiklik düzeltme, aranabilir PDF, taranmış sayfada düzenleme.</summary>
public class OcrTests(ITestOutputHelper log)
{
    private const string Langs = "tur+eng";
    private static readonly string[] Lines =
    {
        "Pijamalı hasta yağız şoföre çabucak güvendi.",
        "İstanbul Büyükşehir Belediyesi, Ankara ve İzmir.",
        "Sözleşme kapsamında ödeme otuz gün içinde yapılır.",
        "Çalışma saatleri sabah dokuz ile akşam altı arasıdır.",
    };

    /// <summary>Metin içeren belgeyi 300 DPI'da çizip “taranmış” görüntü üretir.</summary>
    private static async Task<BgraImage> ScanLikeAsync()
    {
        using var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1);
        await doc.AddTextAsync(0, "Hizmet Sözleşmesi", 72, 760, 24, Fx.Font);
        for (int i = 0; i < Lines.Length; i++) await doc.AddTextAsync(0, Lines[i], 72, 700 - i * 22, 14, Fx.Font);
        return await PdfTools.RenderAtDpiAsync(doc, 0, 300);
    }

    private static void SkipIfNoOcr()
    {
        var why = OcrEngine.Unavailable();
        Assert.True(why is null, why);
        Assert.Contains("tur", OcrEngine.InstalledLanguages());
    }

    private static double Similarity(string a, string b)
    {
        // Levenshtein benzerliği (0–1)
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
    }

    [Fact]
    public async Task RecognizesTurkishText()
    {
        SkipIfNoOcr();
        var img = await ScanLikeAsync();
        var page = await OcrService.RecognizeAsync(img, 300, Langs);
        var text = page.Text;
        log.WriteLine(text);
        foreach (var l in Lines)
        {
            var best = text.Split('\n').Select(t => Similarity(t.Trim(), l)).DefaultIfEmpty(0).Max();
            Assert.True(best >= 0.95, $"“{l}” benzerlik {best:P0}\n{text}");
        }
        Assert.Contains("şoföre", text);
        Assert.Contains("İstanbul", text);
        Assert.True(page.MeanConfidence > 80, $"ortalama güven {page.MeanConfidence:0}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DetectsAndFixesOrientation(int scannedTurns)
    {
        SkipIfNoOcr();
        var img = ImageOps.RotateQuarter(await ScanLikeAsync(), scannedTurns);
        int fix = OcrService.DetectQuarterTurns(img, 300, Langs);
        Assert.Equal((4 - scannedTurns) % 4, fix);
        var (fixedImg, rep) = OcrService.Correct(img, 300, new ImageCorrection(Deskew: false, CleanBorders: false), Langs);
        Assert.Equal(fix, rep.QuarterTurnsApplied);
        Assert.True(fixedImg.Height > fixedImg.Width, "düzeltilen sayfa dikey olmalı");
        var text = (await OcrService.RecognizeAsync(fixedImg, 300, Langs)).Text;
        Assert.Contains("İstanbul", text);
    }

    [Fact]
    public async Task UprightPageIsNotRotated()
    {
        SkipIfNoOcr();
        Assert.Equal(0, OcrService.DetectQuarterTurns(await ScanLikeAsync(), 300, Langs));
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(-2.2)]
    [InlineData(0.8)]
    public async Task MeasuresAndCorrectsSkew(double degrees)
    {
        var straight = await ScanLikeAsync();
        var skewed = ImageOps.Rotate(straight, degrees);
        double est = ImageOps.EstimateSkew(ImageOps.ToGray(skewed));
        Assert.InRange(est, degrees - 0.2, degrees + 0.2);
        var (fixedImg, rep) = OcrService.Correct(skewed, 300, new ImageCorrection(AutoRotate: false, CleanBorders: false));
        Assert.InRange(rep.DeskewDegrees, degrees - 0.2, degrees + 0.2);
        Assert.InRange(ImageOps.EstimateSkew(ImageOps.ToGray(fixedImg)), -0.2, 0.2);
        Assert.Equal(0, ImageOps.EstimateSkew(ImageOps.ToGray(straight)), 1);
    }

    [Fact]
    public void BlankPageHasNoSkew()
    {
        var g = new GrayImage(800, 1100);
        Array.Fill(g.Data, (byte)250);
        Assert.Equal(0, ImageOps.EstimateSkew(g));
    }

    [Fact]
    public async Task CleansDarkScannerBorders()
    {
        var img = await ScanLikeAsync();
        ImageOps.FillRect(img, 0, 0, 60, img.Height, 30, 30, 30);       // sol kapak gölgesi
        ImageOps.FillRect(img, 0, img.Height - 40, img.Width, img.Height, 20, 20, 20); // alt kenar
        Assert.True(ImageOps.CleanBorders(img));
        Assert.Equal(255, img.Pixels[(img.Height / 2) * img.Stride + 10 * 4]);
        Assert.Equal(255, img.Pixels[(img.Height - 5) * img.Stride + (img.Width / 2) * 4]);
        // Metin silinmemiş olmalı
        Assert.True(Fx.InkRatio(img) > 0.002);
        Assert.False(ImageOps.CleanBorders(img));
    }

    /// <summary>Taranmış PDF. quarterTurns ≠ 0: görüntü ters yönde döndürülüp sayfaya /Rotate verilir → görünüm yine dik, kullanıcı alanı döndürülmüş.</summary>
    private static async Task<PdfDocument> ScannedPdfAsync(int quarterTurns = 0)
    {
        var img = ImageOps.RotateQuarter(await ScanLikeAsync(), (4 - quarterTurns) % 4);
        var doc = await PdfDocument.CreateFromImagesAsync(new ImagePageSource[] { new BitmapImageSource(img, 300, 300) }, new ImagePageLayout());
        if (quarterTurns != 0) await doc.RotatePagesAsync(new[] { 0 }, quarterTurns);
        return doc;
    }

    private static async Task<int> OcrPageAsync(PdfDocument doc, int page)
    {
        var img = await PdfTools.RenderAtDpiAsync(doc, page, 300);
        var ocr = await OcrService.RecognizeAsync(img, 300, Langs);
        return await doc.AddOcrLayerAsync(page, ocr, Fx.Font);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task MakesScannedPdfSearchableWithoutChangingLook(int rotation)
    {
        SkipIfNoOcr();
        using var doc = await ScannedPdfAsync(rotation);
        Assert.Equal(0, await doc.CountRealTextCharsAsync(0));
        var before = await PdfTools.RenderAtDpiAsync(doc, 0, 72);
        int words = await OcrPageAsync(doc, 0);
        Assert.True(words > 20);
        Assert.True(await doc.HasOcrLayerAsync(0));
        var after = await PdfTools.RenderAtDpiAsync(doc, 0, 72);
        Assert.Equal(before.Pixels, after.Pixels); // görünmez katman: görünüm değişmez

        var path = Path.Combine(Fx.TempDir(), "aranabilir.pdf");
        await doc.SaveCopyAsync(path);
        using var re = await PdfDocument.OpenAsync(path);
        var hits = await re.SearchAsync("İstanbul", new SearchOptions());
        Assert.Single(hits);
        // Bulunan sözcüğün konumu görüntüdeki yerine denk gelmeli: "İstanbul" ikinci satırın başı.
        var g = await re.GetGeometryAsync(0);
        var r = hits[0].Rects[0];
        var (ux, uy) = g.ToUnit((r.Left + r.Right) / 2, (r.Bottom + r.Top) / 2);
        // Görüntülenen yönde: x ≈ 72pt + yarım sözcük, y ≈ 842-678
        Assert.InRange(ux, 72.0 / 595 - 0.01, 0.30);
        Assert.InRange(uy, (842 - 690.0) / 842 - 0.03, (842 - 670.0) / 842 + 0.03);
        var text = (await re.GetPageTextAsync(0)).Text;
        Assert.Contains("şoföre", text);
        Assert.Equal(words, await re.RemoveOcrLayerAsync(new[] { 0 }));
    }

    [Fact]
    public async Task EditingScannedTextErasesImagePixels()
    {
        SkipIfNoOcr();
        using var doc = await ScannedPdfAsync();
        await OcrPageAsync(doc, 0);
        var paras = await doc.GetParagraphsAsync(0);
        var p = paras.First(x => x.Text.Contains("otuz gün"));
        // OCR katmanından gelen yazı boyutu özgün 14 pt'ye yakın olmalı (düzenlenen metin aynı büyüklükte yazılsın).
        Assert.InRange(p.FontSize, 12.5, 15.5);
        // Paragraf alanındaki mürekkep (görüntüde)
        double Ink(BgraImage im, RectF r)
        {
            int x0 = (int)(r.Left / 595 * im.Width), x1 = (int)(r.Right / 595 * im.Width), y0 = (int)((842 - r.Top) / 842 * im.Height), y1 = (int)((842 - r.Bottom) / 842 * im.Height);
            long ink = 0, n = 0;
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) { n++; if (im.Pixels[y * im.Stride + x * 4] < 128) ink++; }
            return (double)ink / Math.Max(1, n);
        }
        var before = await PdfTools.RenderAtDpiAsync(doc, 0, 150);
        double inkBefore = Ink(before, p.Bounds);
        await doc.ReplaceParagraphAsync(0, p, "Ödeme kırk beş gün içinde yapılır.", new TextStyle(Fx.Font, p.FontSize));
        var info = await doc.GetMainImageAsync(0);
        Assert.NotNull(info);
        var path = Path.Combine(Fx.TempDir(), "duzenlenmis-tarama.pdf");
        await doc.SaveCopyAsync(path);
        using var re = await PdfDocument.OpenAsync(path);
        var text = (await re.GetPageTextAsync(0)).Text;
        Assert.Contains("kırk beş gün", text);
        Assert.DoesNotContain("otuz gün", text);
        // Eski yazı görüntüden silindi: yeni metin daha kısa olduğu için alanın sağ yarısı artık boş.
        var after = await PdfTools.RenderAtDpiAsync(re, 0, 150);
        var right = new RectF(p.Bounds.Left + p.Bounds.Width * 0.7, p.Bounds.Bottom, p.Bounds.Right, p.Bounds.Top);
        Assert.True(Ink(before, right) > 0.02, "önce sağ tarafta yazı vardı");
        Assert.True(Ink(after, right) < 0.002, $"sağ taraf silinmeli (mürekkep {Ink(after, right):P2})");
        Assert.True(inkBefore > 0.02);
        // Görsel nesne eklenmedi (üstüne kutu yok), yalnız görüntü pikselleri değişti.
        var objs = await re.GetPageObjectsAsync(0);
        Assert.Single(objs, o => o.Kind == PageObjectKind.Image);
        Assert.DoesNotContain(objs, o => o.Kind == PageObjectKind.Path);
    }

    [Fact]
    public void WhitensYellowishPaper()
    {
        var img = new BgraImage { Width = 100, Height = 100, Stride = 400, Pixels = new byte[40000] };
        for (int i = 0; i < 10000; i++) { img.Pixels[i * 4] = 200; img.Pixels[i * 4 + 1] = 225; img.Pixels[i * 4 + 2] = 230; img.Pixels[i * 4 + 3] = 255; }
        ImageOps.FillRect(img, 10, 10, 30, 30, 20, 20, 20);
        Assert.True(ImageOps.WhitenBackground(img));
        Assert.True(img.Pixels[(50 * 100 + 50) * 4 + 2] >= 250);
        Assert.True(img.Pixels[(20 * 100 + 20) * 4] < 40);
    }
}
