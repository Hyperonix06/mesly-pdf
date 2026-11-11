using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.Tests;

/// <summary>Test yardımcıları: fikstür yolları, geçici klasör, Türkçe metinli belge üretimi.</summary>
public static class Fx
{
    public const string TurkishPangram = "Pijamalı hasta yağız şoföre çabucak güvendi. İĞÜŞÖÇ ığüşöç İstanbul Iğdır";

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Fixtures");
    public static string F(string name) => Path.Combine(Dir, name);
    public static byte[] Font { get; } = File.ReadAllBytes(F("DejaVuSans.ttf"));

    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "mesly-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>Her sayfasında "Sayfa N" ve isteğe bağlı ek metin olan belge oluşturur.</summary>
    public static async Task<PdfDocument> CreateNumberedAsync(int pages, string? extra = null, SizeF? size = null)
    {
        var doc = await PdfDocument.CreateAsync(size ?? PaperSize.A4.ToPoints(), pages);
        for (int i = 0; i < pages; i++)
        {
            await doc.AddTextAsync(i, $"Sayfa {i + 1}", 72, 760, 20, Font);
            if (extra is not null) await doc.AddTextAsync(i, extra, 72, 720, 12, Font);
        }
        return doc;
    }

    public static async Task<string> SaveNumberedAsync(string dir, string name, int pages, string? extra = null)
    {
        using var doc = await CreateNumberedAsync(pages, extra);
        var path = Path.Combine(dir, name);
        await doc.SaveAsync(path);
        return path;
    }

    /// <summary>Sayfanın ilk satırındaki "Sayfa N" metnini okur.</summary>
    public static async Task<string> FirstLineAsync(PdfDocument doc, int page)
    {
        var t = (await doc.GetPageTextAsync(page)).Text;
        var line = t.Split('\r', '\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        return line.Trim();
    }

    public static async Task<string[]> PageLabelsAsync(PdfDocument doc)
    {
        var list = new string[doc.PageCount];
        for (int i = 0; i < doc.PageCount; i++) list[i] = await FirstLineAsync(doc, i);
        return list;
    }

    /// <summary>Görüntüde beyaz olmayan piksel oranı.</summary>
    public static double InkRatio(BgraImage img)
    {
        long ink = 0, total = (long)img.Width * img.Height;
        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                int i = y * img.Stride + x * 4;
                if (img.Pixels[i] < 200 || img.Pixels[i + 1] < 200 || img.Pixels[i + 2] < 200) ink++;
            }
        return ink / (double)total;
    }
}
