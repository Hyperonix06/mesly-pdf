using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Xunit;

namespace MeslyPdfReader.Tests;

public class PageOperationTests
{
    private static string[] L(params int[] n) => n.Select(i => $"Sayfa {i}").ToArray();

    [Fact]
    public async Task DeletePages()
    {
        using var doc = await Fx.CreateNumberedAsync(5);
        int rev = doc.Revision;
        await doc.DeletePagesAsync(new[] { 1, 3 });
        Assert.Equal(L(1, 3, 5), await Fx.PageLabelsAsync(doc));
        Assert.True(doc.Revision > rev);
        await Assert.ThrowsAsync<PdfException>(() => doc.DeletePagesAsync(new[] { 0, 1, 2 }));
    }

    [Fact]
    public async Task MovePagesLikeDragAndDrop()
    {
        using var doc = await Fx.CreateNumberedAsync(5);
        await doc.MovePagesAsync(new[] { 3, 4 }, 0);
        Assert.Equal(L(4, 5, 1, 2, 3), await Fx.PageLabelsAsync(doc));
        await doc.MovePagesAsync(new[] { 0 }, 5);
        Assert.Equal(L(5, 1, 2, 3, 4), await Fx.PageLabelsAsync(doc));
        await doc.MovePagesAsync(new[] { 1, 3 }, 3);
        Assert.Equal(L(5, 2, 1, 3, 4), await Fx.PageLabelsAsync(doc));
    }

    [Fact]
    public async Task RotatePagesSwapsSizeAndPersists()
    {
        var dir = Fx.TempDir();
        var path = Path.Combine(dir, "dondur.pdf");
        using (var doc = await Fx.CreateNumberedAsync(2))
        {
            var before = doc.GetPageSize(0);
            await doc.RotatePagesAsync(new[] { 0 }, 1);
            var after = doc.GetPageSize(0);
            Assert.Equal(before.Width, after.Height, 1);
            Assert.Equal(before.Height, after.Width, 1);
            Assert.Equal(1, await doc.GetRotationAsync(0));
            await doc.RotatePagesAsync(new[] { 0, 1 }, -1);
            Assert.Equal(0, await doc.GetRotationAsync(0));
            Assert.Equal(3, await doc.GetRotationAsync(1));
            await doc.SaveAsync(path);
        }
        using var re = await PdfDocument.OpenAsync(path);
        Assert.Equal(3, await re.GetRotationAsync(1));
        Assert.True(re.GetPageSize(1).IsLandscape);
    }

    [Fact]
    public async Task InsertBlankAndDuplicate()
    {
        using var doc = await Fx.CreateNumberedAsync(3);
        await doc.InsertBlankPagesAsync(1, 2, PaperSize.A3.ToPoints());
        Assert.Equal(5, doc.PageCount);
        Assert.Equal("A3", PaperSize.Describe(doc.GetPageSize(1)));
        Assert.Equal("", await Fx.FirstLineAsync(doc, 2));
        await doc.DuplicatePagesAsync(new[] { 0, 4 });
        Assert.Equal(new[] { "Sayfa 1", "Sayfa 1", "", "", "Sayfa 2", "Sayfa 3", "Sayfa 3" }, await Fx.PageLabelsAsync(doc));
    }

    [Fact]
    public async Task ImportFromAnotherDocumentAndFile()
    {
        var dir = Fx.TempDir();
        using var doc = await Fx.CreateNumberedAsync(2);
        using var other = await PdfDocument.OpenAsync(Fx.F("v13.pdf"));
        await doc.ImportPagesAsync(other, null, 1);
        Assert.Equal(3, doc.PageCount);
        Assert.Contains("PDF 1.3", await Fx.FirstLineAsync(doc, 1));
        var src = await Fx.SaveNumberedAsync(dir, "kaynak.pdf", 4);
        await doc.InsertFromFileAsync(src, null, new[] { 3, 2 }, doc.PageCount);
        Assert.Equal("Sayfa 4", await Fx.FirstLineAsync(doc, 3));
        Assert.Equal("Sayfa 3", await Fx.FirstLineAsync(doc, 4));
    }

    [Fact]
    public async Task CropAndResize()
    {
        using var doc = await Fx.CreateNumberedAsync(2);
        var a4 = doc.GetPageSize(0);
        await doc.CropPagesAsync(new[] { 0 }, Margins.FromMm(10, 20, 10, 20));
        var cropped = doc.GetPageSize(0);
        Assert.Equal(a4.Width - PaperSize.MmToPt(20), cropped.Width, 1);
        Assert.Equal(a4.Height - PaperSize.MmToPt(40), cropped.Height, 1);
        Assert.Contains("Sayfa 1", (await doc.GetPageTextAsync(0)).Text);

        await doc.ResizePagesAsync(new[] { 1 }, PaperSize.A5.ToPoints());
        Assert.Equal("A5", PaperSize.Describe(doc.GetPageSize(1)));
        var img = await doc.RenderAsync(1, new RenderRequest(420, 595));
        Assert.True(Fx.InkRatio(img) > 0.0005, "Ölçeklenen içerik görünmeli");
        Assert.Contains("Sayfa 2", (await doc.GetPageTextAsync(1)).Text);

        await Assert.ThrowsAsync<PdfException>(() => doc.CropPagesAsync(new[] { 0 }, Margins.FromMm(200, 0, 200, 0)));
    }

    [Fact]
    public async Task CropOnRotatedPageUsesDisplayedEdges()
    {
        using var doc = await Fx.CreateNumberedAsync(1);
        await doc.RotatePagesAsync(new[] { 0 }, 1);
        var s = doc.GetPageSize(0); // yatay
        await doc.CropPagesAsync(new[] { 0 }, new Margins(100, 0, 0, 0)); // görüntülenen soldan 100 pt
        var c = doc.GetPageSize(0);
        Assert.Equal(s.Width - 100, c.Width, 1);
        Assert.Equal(s.Height, c.Height, 1);
    }

    [Fact]
    public async Task ExtractMergeAndSplit()
    {
        var dir = Fx.TempDir();
        using var doc = await Fx.CreateNumberedAsync(7);
        using (var part = await doc.ExtractAsync(new[] { 6, 0 }))
            Assert.Equal(L(7, 1), await Fx.PageLabelsAsync(part));

        var a = await Fx.SaveNumberedAsync(dir, "a.pdf", 3);
        var b = await Fx.SaveNumberedAsync(dir, "b.pdf", 4);
        using var merged = await PdfTools.MergeAsync(new[] { new MergeSource(a), new MergeSource(b, PageRange: "2-3"), new MergeSource(Fx.F("encrypted_rc4.pdf"), "1234", "1") });
        Assert.Equal(6, merged.PageCount);
        Assert.Equal("Sayfa 3", await Fx.FirstLineAsync(merged, 2));
        Assert.Equal("Sayfa 2", await Fx.FirstLineAsync(merged, 3));
        Assert.Contains("Bölüm 1", await Fx.FirstLineAsync(merged, 5));
        Assert.Null(merged.FilePath);

        var outDir = Path.Combine(dir, "bolunmus");
        var files = await PdfTools.SplitAsync(doc, PdfTools.PlanSplit(7, new SplitPlan(SplitMode.EveryNPages, 3)), outDir, "rapor");
        Assert.Equal(3, files.Count);
        using (var last = await PdfDocument.OpenAsync(files[2])) Assert.Equal(L(7), await Fx.PageLabelsAsync(last));
        using (var first = await PdfDocument.OpenAsync(files[0])) Assert.Equal(L(1, 2, 3), await Fx.PageLabelsAsync(first));
        Assert.Contains("rapor_01", Path.GetFileName(files[0]));
    }

    [Fact]
    public async Task MergeReportsWhichFileFailed()
    {
        var ex = await Assert.ThrowsAsync<PdfException>(() => PdfTools.MergeAsync(new[] { new MergeSource(Fx.F("v13.pdf")), new MergeSource(Fx.F("garbage.pdf")) }));
        Assert.Contains("garbage.pdf", ex.Message);
    }
}

public class ExportTests
{
    [Fact]
    public async Task ExportsTextAsUtf8WithBom()
    {
        var dir = Fx.TempDir();
        using var doc = await Fx.CreateNumberedAsync(3, Fx.TurkishPangram);
        var path = Path.Combine(dir, "metin.txt");
        await PdfTools.ExportTextAsync(doc, path);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = File.ReadAllText(path);
        Assert.Equal(3, text.Split(Fx.TurkishPangram).Length - 1);
        Assert.Contains("Sayfa 3", text);
    }

    [Fact]
    public async Task ExportsPngThatIsValid()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("v13.pdf"));
        var img = await PdfTools.RenderAtDpiAsync(doc, 0, 150);
        Assert.Equal((int)Math.Round(595 * 150 / 72.0), img.Width);
        using var ms = new MemoryStream();
        PngEncoder.Write(img, ms, 150);
        var b = ms.ToArray();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, b[..4]);
        Assert.True(b.Length > 1000);
        // PNG'yi tekrar PDF'e çevirip içeriği doğrula (uçtan uca).
        using var back = await PdfDocument.CreateFromImagesAsync(new[] { new BitmapImageSource(img, 150, 150) }, new ImagePageLayout());
        Assert.InRange(back.GetPageSize(0).Width, 594.5, 595.5);
        var re = await back.RenderAsync(0, new RenderRequest(595, 842));
        Assert.InRange(Fx.InkRatio(re), 0.0005, 0.3);
    }

    [Fact]
    public async Task JpegAndBitmapImagesToPdf()
    {
        var dir = Fx.TempDir();
        var jpeg = new JpegImageSource(File.ReadAllBytes(Fx.F("photo.jpg")), 150, 150);
        var bmp = new BgraImage { Width = 50, Height = 100, Stride = 200, Pixels = Enumerable.Repeat((byte)60, 200 * 100).ToArray() };
        using var doc = await PdfDocument.CreateFromImagesAsync(new ImagePageSource[] { jpeg, new BitmapImageSource(bmp, 72, 72) }, new ImagePageLayout());
        Assert.Equal(2, doc.PageCount);
        Assert.Equal(640 * 72 / 150.0, doc.GetPageSize(0).Width, 1);
        Assert.Equal(50, doc.GetPageSize(1).Width, 1);
        var path = Path.Combine(dir, "gorsel.pdf");
        await doc.SaveAsync(path);
        Assert.True(new FileInfo(path).Length < 60_000, "JPEG yeniden sıkıştırılmadan gömülmeli");

        using var a4 = await PdfDocument.CreateFromImagesAsync(new[] { jpeg }, new ImagePageLayout(PaperSize.A4.ToPoints(), 36, AutoRotate: true));
        Assert.Equal("A4 yatay", PaperSize.Describe(a4.GetPageSize(0)));
        var r = await a4.RenderAsync(0, new RenderRequest(842, 595));
        // Sayfa ortasında fotoğrafın arka plan rengi (RGB 240,244,250), kenar boşluğunda beyaz olmalı.
        int c = (r.Height / 2) * r.Stride + (r.Width / 2) * 4;
        Assert.InRange(r.Pixels[c + 2], 232, 248);
        Assert.InRange(r.Pixels[c], 244, 254);
        Assert.Equal(255, r.Pixels[10 * r.Stride + 10 * 4]);
        Assert.True(Fx.InkRatio(r) > 0.005, "Mavi çerçeve görünmeli");
    }
}

public class GeometryTests
{
    [Theory]
    [InlineData(0, 0)] [InlineData(1, 0)] [InlineData(2, 0)] [InlineData(3, 0)]
    [InlineData(0, 1)] [InlineData(1, 3)] [InlineData(2, 2)]
    public async Task MatchesPdfiumDisplayMatrix(int pageRotation, int viewTurns)
    {
        using var doc = await Fx.CreateNumberedAsync(1);
        await doc.CropPagesAsync(new[] { 0 }, new Margins(30, 50, 10, 20)); // köken (0,0) olmasın
        if (pageRotation > 0) await doc.RotatePagesAsync(new[] { 0 }, pageRotation);
        var g = await doc.GetGeometryAsync(0);
        Assert.Equal(pageRotation, g.Rotation);
        var size = doc.GetPageSize(0);
        bool swap = viewTurns % 2 == 1;
        int w = (int)Math.Round(swap ? size.Height : size.Width), h = (int)Math.Round(swap ? size.Width : size.Height);
        foreach (var (x, y) in new[] { (g.Box.Left + 15, g.Box.Bottom + 40), (g.Box.Right - 60, g.Box.Top - 5), (300.0, 400.0) })
        {
            var (dx, dy) = await doc.PageToDeviceAsync(0, w, h, viewTurns, x, y);
            var (u, v) = g.ToUnit(x, y, viewTurns);
            Assert.InRange(u * w, dx - 1.5, dx + 1.5);
            Assert.InRange(v * h, dy - 1.5, dy + 1.5);
            var (bx, by) = g.FromUnit(u, v, viewTurns);
            Assert.Equal(x, bx, 3); Assert.Equal(y, by, 3);
        }
    }
}
