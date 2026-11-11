using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Xunit;

namespace MeslyPdfReader.Tests;

public class CompressTests
{
    /// <summary>Fotoğraf benzeri yumuşak geçişli, gürültülü sentetik görüntü.</summary>
    private static BgraImage Photo(int w, int h)
    {
        var px = new byte[w * h * 4];
        var rnd = new Random(7);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = (y * w + x) * 4;
                double fx = (double)x / w, fy = (double)y / h;
                px[o] = (byte)Math.Clamp(255 * fx + rnd.Next(-6, 7), 0, 255);
                px[o + 1] = (byte)Math.Clamp(255 * fy + rnd.Next(-6, 7), 0, 255);
                px[o + 2] = (byte)Math.Clamp(128 + 100 * Math.Sin(fx * 9) * Math.Cos(fy * 7), 0, 255);
                px[o + 3] = 255;
            }
        return new BgraImage { Width = w, Height = h, Stride = w * 4, Pixels = px };
    }

    private static double MeanDiff(BgraImage a, BgraImage b)
    {
        long sum = 0;
        for (int i = 0; i < a.Pixels.Length; i += 4)
            sum += Math.Abs(a.Pixels[i] - b.Pixels[i]) + Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1]) + Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]);
        return sum / (a.Pixels.Length / 4 * 3.0);
    }

    [Fact]
    public async Task DownsamplesDenseImagesAndKeepsAppearance()
    {
        var dir = Fx.TempDir();
        var src = Path.Combine(dir, "big.pdf");
        using (var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1))
        {
            // 1600×1200 piksel, 2×1,5 inç alana: ~800 dpi.
            await doc.AddImageAsync(0, new BitmapImageSource(Photo(1600, 1200)), new RectF(72, 400, 216, 508));
            await doc.AddTextAsync(0, "Metin korunmalı", 72, 760, 14, Fx.Font);
            await doc.SaveAsync(src);
        }
        var before = await Render(src);
        var dst = Path.Combine(dir, "small.pdf");
        var r = await PdfTools.CompressFileAsync(src, dst, CompressOptions.Medium);

        Assert.True(r.Written);
        Assert.Equal(1, r.ImagesSeen);
        Assert.Equal(1, r.ImagesRecompressed);
        Assert.True(r.BytesAfter < r.BytesBefore / 4, $"{r.BytesBefore} → {r.BytesAfter}");
        Assert.Equal(r.BytesAfter, new FileInfo(dst).Length);
        Assert.Equal(r.BytesBefore, new FileInfo(src).Length); // özgün dosya değişmedi

        using var outDoc = await PdfDocument.OpenAsync(dst);
        Assert.Contains("Metin korunmalı", (await outDoc.GetPageTextAsync(0)).Text);
        var objs = await outDoc.GetPageObjectsAsync(0);
        var img = Assert.Single(objs, o => o.Kind == PageObjectKind.Image);
        Assert.InRange(img.Bounds.Width, 143, 145); // konum ve boyut korunur
        Assert.InRange(img.Bounds.Bottom, 399, 401);
        var after = await Render(dst);
        Assert.True(MeanDiff(before, after) < 6, $"fark {MeanDiff(before, after):F2}");
    }

    [Fact]
    public async Task DoesNotWriteWhenNothingToGain()
    {
        var dir = Fx.TempDir();
        var src = await Fx.SaveNumberedAsync(dir, "text.pdf", 3, Fx.TurkishPangram);
        var dst = Path.Combine(dir, "out.pdf");
        var r = await PdfTools.CompressFileAsync(src, dst, CompressOptions.Small);
        Assert.False(r.Written, $"{r.BytesBefore} → {r.BytesAfter}");
        Assert.Equal(0, r.ImagesRecompressed);
        Assert.False(File.Exists(dst));
        Assert.Empty(Directory.GetFiles(dir, "*.mesly-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LeavesTransparentAndSmallImagesAlone()
    {
        // 1000×750 SMask'lı görsel 120×90 pt alanda (~600 dpi): yoğun ama saydam → dokunulmamalı.
        using var doc = await PdfDocument.OpenAsync(Fx.F("masked_image.pdf"));
        var before = await PdfTools.RenderAtDpiAsync(doc, 0, 60);
        await doc.AddImageAsync(0, new BitmapImageSource(Photo(40, 30)), new RectF(300, 400, 305, 404));
        var (seen, changed) = await doc.CompressImagesAsync(CompressOptions.Small);
        Assert.Equal(2, seen);
        Assert.Equal(0, changed);
        var after = await PdfTools.RenderAtDpiAsync(doc, 0, 60);
        Assert.True(MeanDiff(before, after) < 1.5);
    }

    [Fact]
    public async Task RefusesToOverwriteSource()
    {
        var dir = Fx.TempDir();
        var src = await Fx.SaveNumberedAsync(dir, "a.pdf", 1);
        await Assert.ThrowsAsync<PdfException>(() => PdfTools.CompressFileAsync(src, src, CompressOptions.Medium));
    }

    private static async Task<BgraImage> Render(string path)
    {
        using var d = await PdfDocument.OpenAsync(path);
        return await PdfTools.RenderAtDpiAsync(d, 0, 50);
    }
}
