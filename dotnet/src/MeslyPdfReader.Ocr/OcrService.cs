using System.Collections.Concurrent;
using MeslyPdfReader.Core;

namespace MeslyPdfReader.Ocr;

/// <summary>OCR motoru havuzu ve görüntü düzeltme hattı. İş parçacığı güvenlidir.</summary>
public static class OcrService
{
    private static readonly ConcurrentDictionary<string, ConcurrentBag<OcrEngine>> Pool = new();
    /// <summary>Aynı anda çalışan tanıma sayısı (her motor ~100 MB bellek kullanır).</summary>
    public static int MaxParallel { get; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    private static readonly SemaphoreSlim Slots = new(MaxParallel, MaxParallel);

    private static OcrEngine Rent(string langs) =>
        Pool.GetOrAdd(langs, _ => new ConcurrentBag<OcrEngine>()).TryTake(out var e) ? e : OcrEngine.Create(langs);

    private static void Return(OcrEngine e) => Pool.GetOrAdd(e.Languages, _ => new ConcurrentBag<OcrEngine>()).Add(e);

    public static async Task<OcrPage> RecognizeAsync(BgraImage img, int dpi, string languages, CancellationToken ct = default, Action<double>? progress = null)
    {
        await Slots.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                var gray = ImageOps.ToGray(img);
                var e = Rent(languages);
                try { return e.Recognize(gray, dpi, ct, progress); }
                finally { Return(e); }
            }, ct);
        }
        finally { Slots.Release(); }
    }

    /// <summary>
    /// Taranmış görüntüyü düzeltir: yön (OCR yön algılaması; emin olunamazsa dört yönde hızlı tanıma karşılaştırması),
    /// eğiklik, kenar gölgesi, arka plan. Görüntü yerinde değişebilir; yeni görüntü döner.
    /// </summary>
    public static (BgraImage Image, CorrectionReport Report) Correct(BgraImage img, int dpi, ImageCorrection c, string languages = "tur+eng", CancellationToken ct = default)
    {
        int turns = 0; double skew = 0; bool borders = false, whiten = false; string? note = null;
        if (c.CleanBorders) borders = ImageOps.CleanBorders(img);
        if (c.AutoRotate)
        {
            if (OcrEngine.Unavailable() is { } why) note = why;
            else
            {
                turns = DetectQuarterTurns(img, dpi, languages, ct);
                if (turns != 0) img = ImageOps.RotateQuarter(img, turns);
            }
        }
        ct.ThrowIfCancellationRequested();
        if (c.Deskew)
        {
            skew = ImageOps.EstimateSkew(ImageOps.ToGray(img));
            // Satırlar sağa doğru aşağı iniyorsa (skew > 0) görüntü saat yönünün tersine döndürülür.
            if (Math.Abs(skew) >= 0.15) img = ImageOps.Rotate(img, -skew); else skew = 0;
        }
        if (c.WhitenBackground) whiten = ImageOps.WhitenBackground(img);
        return (img, new CorrectionReport(turns, skew, borders, whiten, note));
    }

    /// <summary>Saat yönünde kaç çeyrek tur döndürülmesi gerektiği (0–3).</summary>
    public static int DetectQuarterTurns(BgraImage img, int dpi, string languages = "tur+eng", CancellationToken ct = default)
    {
        // Yön algılama için ~150 DPI yeterli ve hızlı.
        var gray = ImageOps.ToGray(img);
        var small = ImageOps.Downscale(gray, 2000, out double f);
        int sdpi = (int)Math.Max(70, dpi / f);
        var osd = OcrEngine.DetectOrientation(small, sdpi);
        if (osd is { } o && o.Confidence >= 2.0) return o.RotateClockwise / 90;
        // Emin değil: dört yönde hızlı tanıma, en çok güvenilir sözcük veren yön seçilir.
        var tiny = ImageOps.Downscale(gray, 1500, out double f2);
        int tdpi = (int)Math.Max(70, dpi / f2);
        var scores = new double[4];
        var e = Rent(languages);
        try
        {
            var bg = ImageOps.FromGray(tiny);
            for (int q = 0; q < 4; q++)
            {
                ct.ThrowIfCancellationRequested();
                var g = ImageOps.ToGray(ImageOps.RotateQuarter(bg, q));
                var page = e.Recognize(g, tdpi, ct);
                scores[q] = page.Words.Where(w => w.Confidence >= 60 && w.Text.Count(char.IsLetter) >= 3).Sum(w => w.Text.Length * (w.Confidence / 100.0));
            }
        }
        finally { Return(e); }
        int best = Array.IndexOf(scores, scores.Max());
        // Belirgin fark yoksa dokunma.
        double second = scores.Where((_, i) => i != best).DefaultIfEmpty(0).Max();
        return scores[best] > 8 && scores[best] > second * 1.5 ? best : 0;
    }
}
