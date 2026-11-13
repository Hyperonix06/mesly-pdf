using MeslyPdfReader.Core;
using MeslyPdfReader.Ocr;
using MeslyPdfReader.Pdf;
using Xunit;
using Xunit.Abstractions;

namespace MeslyPdfReader.Tests;

/// <summary>Gelişmiş OCR (PP-OCRv6 + ONNX Runtime): gerçek modellerle doğruluk, kutu geometrisi, okuma sırası ve Tesseract karşılaştırması.</summary>
public class PaddleOcrTests(ITestOutputHelper log)
{
    private static readonly string[] Lines =
    {
        "Pijamalı hasta yağız şoföre çabucak güvendi.",
        "İstanbul Büyükşehir Belediyesi, Ankara ve İzmir.",
        "Sözleşme kapsamında ödeme otuz gün içinde yapılır.",
        "Çalışma saatleri sabah dokuz ile akşam altı arasıdır.",
        "ÖĞRENCİLER ÜÇ ŞEHİRDE ÇALIŞIYOR, IĞDIR VE ISPARTA'DA.",
        "Fatura No: 2026/00417 — Tutar: 1.250,75 TL (KDV %20 dahil)",
        "Müşteri Ayşe Yılmaz, Çankaya Mah. 1453. Sok. No:7/3 Ankara",
    };

    private static void Need()
    {
        var why = PaddleOcr.Unavailable();
        Assert.True(why is null, why);
    }

    private static async Task<BgraImage> PageAsync(double fontPt = 14, int dpi = 300)
    {
        using var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1);
        await doc.AddTextAsync(0, "Hizmet Sözleşmesi", 72, 780, fontPt + 10, Fx.Font);
        for (int i = 0; i < Lines.Length; i++) await doc.AddTextAsync(0, Lines[i], 72, 730 - i * (fontPt + 8), fontPt, Fx.Font);
        return await PdfTools.RenderAtDpiAsync(doc, 0, dpi);
    }

    /// <summary>Tarama benzeri bozulma: bulanıklık, gürültü, kontrast kaybı ve gölge.</summary>
    private static BgraImage Degrade(BgraImage src, int blur, double noise, double contrast, double shade)
    {
        var rnd = new Random(7);
        var px = (byte[])src.Pixels.Clone();
        int w = src.Width, h = src.Height, stride = src.Stride;
        if (blur > 0)
        {
            var tmp = (byte[])px.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int sum = 0, n = 0;
                    for (int dx = -blur; dx <= blur; dx++) { int xx = Math.Clamp(x + dx, 0, w - 1); sum += tmp[y * stride + xx * 4]; n++; }
                    for (int c = 0; c < 3; c++) px[y * stride + x * 4 + c] = (byte)(sum / n);
                }
        }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double v = px[y * stride + x * 4];
                v = 255 - (255 - v) * contrast;                      // kontrast kaybı (siyah → koyu gri)
                v *= 1 - shade * (x / (double)w);                    // soldan sağa gölge
                v += (rnd.NextDouble() - 0.5) * 2 * noise;           // gürültü
                byte b = (byte)Math.Clamp((int)v, 0, 255);
                px[y * stride + x * 4] = px[y * stride + x * 4 + 1] = px[y * stride + x * 4 + 2] = b; px[y * stride + x * 4 + 3] = 255;
            }
        return new BgraImage { Width = w, Height = h, Stride = stride, Pixels = px };
    }

    private static double Similarity(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
    }

    /// <summary>Her referans satırın OCR çıktısındaki en iyi eşleşmesinin ortalama benzerliği.</summary>
    private static double Score(string text)
    {
        var got = text.Split('\n').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        return Lines.Average(l => got.Select(t => Similarity(t, l)).DefaultIfEmpty(0).Max());
    }

    [Fact]
    public async Task RecognizesTurkishPageExactly()
    {
        Need();
        var img = await PageAsync();
        var page = await OcrService.RecognizeAsync(img, 300, "tur+eng", engine: OcrBackend.Advanced);
        log.WriteLine(page.Text);
        var score = Score(page.Text);
        log.WriteLine($"PaddleOCR benzerlik: {score:P1}");
        Assert.True(score >= 0.97, $"benzerlik {score:P1}\n{page.Text}");
        Assert.Contains("Hizmet Sözleşmesi", page.Text);
        Assert.True(page.MeanConfidence > 85, $"ortalama güven {page.MeanConfidence:0}");
    }

    [Fact]
    public async Task WordBoxesAreOrderedAndInsidePage()
    {
        Need();
        var img = await PageAsync();
        var page = await OcrService.RecognizeAsync(img, 300, "tur+eng", engine: OcrBackend.Advanced);
        Assert.NotEmpty(page.Words);
        foreach (var w in page.Words)
        {
            Assert.InRange(w.Left, 0, img.Width); Assert.InRange(w.Right, w.Left + 1, img.Width);
            Assert.InRange(w.Top, 0, img.Height); Assert.InRange(w.Bottom, w.Top + 1, img.Height);
            Assert.InRange(w.BaselineY, w.Top, w.Bottom);
        }
        // Satırlar yukarıdan aşağıya.
        var lineTops = page.Words.GroupBy(w => w.Line).OrderBy(g => g.Key).Select(g => g.Min(w => w.Top)).ToList();
        Assert.Equal(lineTops.OrderBy(t => t).ToList(), lineTops);
        // Aynı satırdaki sözcükler soldan sağa ve çakışmadan.
        foreach (var g in page.Words.GroupBy(w => w.Line))
        {
            var ws = g.ToList();
            for (int i = 1; i < ws.Count; i++) Assert.True(ws[i].Left >= ws[i - 1].Left, $"“{ws[i - 1].Text}” → “{ws[i].Text}”");
        }
        // “Ankara” sözcüğünün kutusu gerçekten o metnin üzerinde (PDF katmanında tıklanabilir/aranabilir olması için).
        var ankara = page.Words.First(w => w.Text.StartsWith("Ankara"));
        var crop = Crop(img, ankara);
        Assert.True(crop > 0.02 && crop < 0.5, $"kutu mürekkep oranı {crop:P1}");
    }

    private static double Crop(BgraImage img, OcrWord w)
    {
        int dark = 0, n = 0;
        for (int y = w.Top; y < w.Bottom; y++)
            for (int x = w.Left; x < w.Right; x++) { n++; if (img.Pixels[y * img.Stride + x * 4] < 128) dark++; }
        return (double)dark / Math.Max(1, n);
    }

    [Theory]
    [InlineData("temiz", 0, 0.0, 1.0, 0.0)]
    [InlineData("bulanık", 2, 6.0, 0.8, 0.0)]
    [InlineData("gürültülü", 1, 28.0, 0.65, 0.2)]
    [InlineData("soluk ve gölgeli", 1, 14.0, 0.45, 0.35)]
    public async Task HybridIsAtLeastAsGoodAsEitherEngine(string name, int blur, double noise, double contrast, double shade)
    {
        Need();
        var tess = OcrEngine.Unavailable();
        Assert.True(tess is null, tess);
        var img = Degrade(await PageAsync(), blur, noise, contrast, shade);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hyb = await OcrService.RecognizeAsync(img, 300, "tur+eng", engine: OcrBackend.Hybrid);
        var tHyb = sw.Elapsed; sw.Restart();
        var adv = await OcrService.RecognizeAsync(img, 300, "tur+eng", engine: OcrBackend.Advanced);
        var tAdv = sw.Elapsed; sw.Restart();
        var std = await OcrService.RecognizeAsync(img, 300, "tur+eng", engine: OcrBackend.Standard);
        var tStd = sw.Elapsed;
        double h = Score(hyb.Text), a = Score(adv.Text), s = Score(std.Text);
        log.WriteLine($"[{name}] Hibrit {h:P1} ({tHyb.TotalSeconds:0.0} sn) · PaddleOCR {a:P1} ({tAdv.TotalSeconds:0.0} sn) · Tesseract {s:P1} ({tStd.TotalSeconds:0.0} sn)");
        log.WriteLine("— Hibrit —\n" + hyb.Text);
        Assert.True(h >= Math.Max(a, s) - 0.005, $"[{name}] hibrit {h:P1}, PaddleOCR {a:P1}, Tesseract {s:P1}");
        Assert.True(h >= 0.95, $"[{name}] hibrit yetersiz: {h:P1}\n{hyb.Text}");
    }

    [Fact]
    public void MergeFixesTurkishLettersOnlyWhereTesseractIsSure()
    {
        static OcrWord W(string t, int l, float c) => new(t, l, 10, l + 100, 40, c, 0, 0, 34);
        var adv = new OcrPage(1000, 100, 300, new[] { W("istanbul", 0, 99), W("Yilmaz", 120, 90), W("%20", 240, 99.5f), W("Cumhuriyet", 360, 80), W("lorem", 480, 70) }, "tur");
        var tess = new OcrPage(1000, 100, 300, new[] { W("İstanbul", 0, 95), W("Yılmaz", 120, 40), W("20", 240, 96), W("Cumhuriyeti", 360, 93), W("ipsum", 480, 95), W("Ekstra", 700, 96), new OcrWord("Yalnız", 0, 300, 100, 330, 96, 5, 5, 325) }, "tur");
        var r = HybridOcr.Merge(adv, tess);
        var texts = r.Page.Words.Select(w => w.Text).ToList();
        Assert.Equal("İstanbul", texts[0]);          // yalnız İ/i farkı, Tesseract güvenli → Tesseract
        Assert.Equal("Yilmaz", texts[1]);            // Tesseract güveni düşük (40) → gelişmiş kalır
        Assert.Equal("%20", texts[2]);               // gerçek fark, gelişmiş çok emin → gelişmiş kalır
        Assert.Equal("Cumhuriyeti", texts[3]);       // gerçek fark, gelişmiş emin değil, Tesseract çok emin → Tesseract
        Assert.Equal("lorem", texts[4]);             // çok farklı sözcük (mesafe büyük) → değişmez
        Assert.Contains("Ekstra", texts);            // gelişmiş motorun kaçırdığı, aynı satırdaki emin sözcük eklenir
        Assert.DoesNotContain("Yalnız", texts);      // satırı olmayan sözcük eklenmez (okuma sırası bozulmasın)
        Assert.Equal(2, r.Replaced); Assert.Equal(1, r.Added);
    }

    [Fact]
    public void ConfusableDistanceIgnoresLookAlikeLetters()
    {
        Assert.Equal(0, HybridOcr.Distance("ÖČRENCILER", "ÖĞRENCİLER"));
        Assert.Equal(0, HybridOcr.Distance("$EHIRDE", "ŞEHİRDE"));
        Assert.Equal(1, HybridOcr.Distance("yağız", "yağızz"));
        Assert.True(HybridOcr.Distance("kitap", "defter") >= 4);
    }

    [Fact]
    public async Task ReadsSmallText()
    {
        Need();
        var img = await PageAsync(fontPt: 8.5, dpi: 300);
        var page = await OcrService.RecognizeAsync(img, 300, "tur+eng", engine: OcrBackend.Advanced);
        var s = Score(page.Text);
        log.WriteLine($"8,5 pt: {s:P1}\n{page.Text}");
        Assert.True(s >= 0.9, $"8,5 pt benzerlik {s:P1}");
    }

    [Fact]
    public async Task ReadsLowResolutionScan()
    {
        Need();
        var img = await PageAsync(dpi: 150);
        var page = await OcrService.RecognizeAsync(img, 150, "tur+eng", engine: OcrBackend.Advanced);
        var s = Score(page.Text);
        log.WriteLine($"150 DPI: {s:P1}");
        Assert.True(s >= 0.9, $"150 DPI benzerlik {s:P1}");
    }

    [Fact]
    public async Task KeepsTwoColumnsInReadingOrder()
    {
        Need();
        using var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1);
        string[] left = { "Sol sütun birinci satır metni burada", "Sol sütun ikinci satır metni burada", "Sol sütun üçüncü satır metni burada", "Sol sütun dördüncü satır metni burada" };
        string[] right = { "Sağ sütun birinci satır metni burada", "Sağ sütun ikinci satır metni burada", "Sağ sütun üçüncü satır metni burada", "Sağ sütun dördüncü satır metni burada" };
        for (int i = 0; i < 4; i++)
        {
            await doc.AddTextAsync(0, left[i], 60, 700 - i * 20, 11, Fx.Font);
            await doc.AddTextAsync(0, right[i], 320, 700 - i * 20, 11, Fx.Font);
        }
        var img = await PdfTools.RenderAtDpiAsync(doc, 0, 300);
        var page = await OcrService.RecognizeAsync(img, 300, "tur+eng", engine: OcrBackend.Advanced);
        var text = page.Text;
        log.WriteLine(text);
        int lastLeft = text.IndexOf("Sol sütun dördüncü", StringComparison.Ordinal), firstRight = text.IndexOf("Sağ sütun birinci", StringComparison.Ordinal);
        Assert.True(lastLeft >= 0 && firstRight >= 0, text);
        Assert.True(lastLeft < firstRight, "sol sütun bitmeden sağ sütun başlamamalı:\n" + text);
    }

    [Fact]
    public async Task BlankPageGivesNoWords()
    {
        Need();
        var px = new byte[800 * 4 * 600]; Array.Fill(px, (byte)255);
        var page = await OcrService.RecognizeAsync(new BgraImage { Width = 800, Height = 600, Stride = 3200, Pixels = px }, 300, "tur+eng", engine: OcrBackend.Advanced);
        Assert.Empty(page.Words);
    }

    [Fact]
    public async Task CancelsPromptly()
    {
        Need();
        var img = await PageAsync();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OcrService.RecognizeAsync(img, 300, "tur+eng", cts.Token, engine: OcrBackend.Advanced));
    }
}
