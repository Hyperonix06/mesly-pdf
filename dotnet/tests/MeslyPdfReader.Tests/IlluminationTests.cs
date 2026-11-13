using MeslyPdfReader.Core;
using MeslyPdfReader.Ocr;
using MeslyPdfReader.Pdf;
using Xunit;

namespace MeslyPdfReader.Tests;

/// <summary>Aydınlık düzleştirme: gölge ve ışık degradesi giderilir, mürekkep ve düzgün sayfalar korunur.</summary>
public class IlluminationTests
{
    private static async Task<BgraImage> PageAsync()
    {
        using var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1);
        await doc.AddTextAsync(0, "Hizmet Sözleşmesi", 72, 760, 24, Fx.Font);
        string[] lines = { "Pijamalı hasta yağız şoföre çabucak güvendi.", "İstanbul Büyükşehir Belediyesi, Ankara ve İzmir.", "Sözleşme kapsamında ödeme otuz gün içinde yapılır.", "Çalışma saatleri sabah dokuz ile akşam altı arasıdır." };
        for (int i = 0; i < lines.Length; i++) await doc.AddTextAsync(0, lines[i], 72, 700 - i * 22, 14, Fx.Font);
        return await PdfTools.RenderAtDpiAsync(doc, 0, 200);
    }

    /// <summary>Soldan sağa 1.0→0.45 degrade + sol alt köşede yuvarlak gölge.</summary>
    private static void Shade(BgraImage img)
    {
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                double k = 1.0 - 0.55 * x / img.Width;
                double dx = (x - img.Width * 0.15) / (img.Width * 0.3), dy = (y - img.Height * 0.85) / (img.Height * 0.2);
                k *= 1.0 - 0.3 * Math.Max(0, 1 - Math.Sqrt(dx * dx + dy * dy));
                int o = y * img.Stride + x * 4;
                for (int c = 0; c < 3; c++) img.Pixels[o + c] = (byte)(img.Pixels[o + c] * k);
            }
    }

    private static double PaperLevel(BgraImage img, double fx, double fy)
    {
        // Yakındaki en parlak %20'nin ortalaması (mürekkep dışı kâğıt).
        int cx = (int)(img.Width * fx), cy = (int)(img.Height * fy), r = img.Width / 40;
        var v = new List<int>();
        for (int y = Math.Max(0, cy - r); y < Math.Min(img.Height, cy + r); y++)
            for (int x = Math.Max(0, cx - r); x < Math.Min(img.Width, cx + r); x++) v.Add(img.Pixels[y * img.Stride + x * 4 + 1]);
        v.Sort(); return v.Skip(v.Count * 8 / 10).Average();
    }

    [Fact]
    public async Task RemovesShadowsAndGradient()
    {
        var img = await PageAsync();
        Shade(img);
        double[] fx = { 0.1, 0.5, 0.9, 0.15 }, fy = { 0.1, 0.5, 0.1, 0.85 };
        double spreadBefore = Enumerable.Range(0, 4).Select(i => PaperLevel(img, fx[i], fy[i])).ToList() is var a ? a.Max() - a.Min() : 0;
        Assert.True(spreadBefore > 80, $"deneme görüntüsü yeterince gölgeli değil ({spreadBefore:0})");
        Assert.True(ImageOps.FlattenIllumination(img));
        var after = Enumerable.Range(0, 4).Select(i => PaperLevel(img, fx[i], fy[i])).ToList();
        Assert.True(after.Max() - after.Min() < 12, $"kâğıt hâlâ düzgün değil: {string.Join(", ", after.Select(v => v.ToString("0")))}");
        Assert.All(after, v => Assert.True(v > 235, $"kâğıt yeterince beyaz değil: {v:0}"));
        Assert.True(Fx.InkRatio(img) > 0.01, "mürekkep silinmiş");
    }

    [Fact]
    public async Task LeavesEvenPageAlone()
    {
        var img = await PageAsync();
        var before = (byte[])img.Pixels.Clone();
        Assert.False(ImageOps.FlattenIllumination(img));
        Assert.Equal(before, img.Pixels);
    }

    [Fact]
    public async Task InkStaysDarkAndShapeIsKept()
    {
        var clean = await PageAsync();
        var img = await PageAsync();
        Shade(img);
        ImageOps.FlattenIllumination(img);
        // Düzleştirilmiş görüntü temizle aynı mürekkep oranına ±%25 içinde yakın olmalı (yeni beneklik/silinme yok).
        double a = Fx.InkRatio(clean), b = Fx.InkRatio(img);
        Assert.InRange(b / a, 0.75, 1.25);
    }

    [Fact]
    public async Task CorrectAppliesItWhenWhiteningIsOn()
    {
        var img = await PageAsync();
        Shade(img);
        var (fixedImg, rep) = OcrService.Correct(img, 200, new ImageCorrection(AutoRotate: false, Deskew: false, CleanBorders: false, WhitenBackground: true));
        Assert.True(rep.BackgroundWhitened);
        Assert.True(PaperLevel(fixedImg, 0.9, 0.1) > 235);
    }

    [Fact]
    public async Task ImprovesOcrOnShadedPage()
    {
        if (OcrService.Unavailable("tur+eng", OcrBackend.Auto) is { } why) Assert.Fail(why);
        string[] lines = { "Pijamalı hasta yağız şoföre çabucak güvendi.", "İstanbul Büyükşehir Belediyesi, Ankara ve İzmir.", "Sözleşme kapsamında ödeme otuz gün içinde yapılır.", "Çalışma saatleri sabah dokuz ile akşam altı arasıdır." };
        double Score(OcrPage p)
        {
            var got = p.Words.GroupBy(w => w.Line).Select(g => string.Join(" ", g.Select(w => w.Text))).ToList();
            return lines.Average(l => got.Select(t => 1 - (double)Lev(t, l) / Math.Max(t.Length, l.Length)).DefaultIfEmpty(0).Max());
        }
        var img = await PageAsync(); Shade(img);
        var gray = ImageOps.ToGray(img);
        double before = Score(await OcrService.RecognizeAsync(img, 200, "tur+eng"));
        ImageOps.FlattenIllumination(img);
        double after = Score(await OcrService.RecognizeAsync(img, 200, "tur+eng"));
        Assert.True(after >= before - 0.01, $"düzleştirme okumayı bozdu: {before:P1} → {after:P1}");
        Assert.True(after >= 0.93, $"düzleştirilmiş sayfa okunamadı: {after:P1}");
    }

    private static int Lev(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++) for (int j = 1; j <= b.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
