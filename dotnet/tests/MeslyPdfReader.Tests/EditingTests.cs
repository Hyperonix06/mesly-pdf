using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Xunit;

namespace MeslyPdfReader.Tests;

/// <summary>Aşama 3: gerçek içerik düzenleme. Her test sonucu kaydedip yeniden açarak doğrular.</summary>
public class EditingTests
{
    private static TextStyle Style(double size = 11, TextAlign align = TextAlign.Left, bool underline = false) => new(Fx.Font, size, 0x000000, align, underline);

    private static async Task<PdfDocument> ReopenAsync(PdfDocument doc)
    {
        var path = Path.Combine(Fx.TempDir(), "edit.pdf");
        await doc.SaveCopyAsync(path);
        doc.Dispose();
        return await PdfDocument.OpenAsync(path);
    }

    [Fact]
    public async Task EnumeratesObjectsOfThirdPartyPdf()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("edit_paragraphs.pdf"));
        var objs = await doc.GetPageObjectsAsync(0);
        Assert.Contains(objs, o => o.Kind == PageObjectKind.Image);
        Assert.Contains(objs, o => o.Kind == PageObjectKind.Path && o.FillRgb == 0x3366CC);
        var title = objs.First(o => o.Kind == PageObjectKind.Text && o.Text!.Contains("Sözleşme"));
        Assert.Equal(20, title.FontSize, 1);
        Assert.Contains("DejaVu", title.FontName);
    }

    [Fact]
    public async Task GroupsLinesIntoParagraphs()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("edit_paragraphs.pdf"));
        var paras = await doc.GetParagraphsAsync(0);
        var p1 = paras.Single(p => p.Text.StartsWith("Bu metin"));
        Assert.Equal(3, p1.ObjectIndices.Count);
        Assert.EndsWith("paragraf sona erer.", p1.Text);
        Assert.Contains("düzenleme testi için", p1.Text);
        Assert.Equal(14, p1.LineHeight, 1);
        Assert.Contains(paras, p => p.Text.StartsWith("Sözleşme Başlığı"));
        Assert.Contains(paras, p => p.Text.StartsWith("İkinci paragraf"));
        Assert.Equal(3, paras.Count);
    }

    [Fact]
    public async Task ReplaceParagraphRemovesOldTextAndReflows()
    {
        var doc = await PdfDocument.OpenAsync(Fx.F("edit_paragraphs.pdf"));
        var p1 = (await doc.GetParagraphsAsync(0)).Single(p => p.Text.StartsWith("Bu metin"));
        int before = (await doc.GetPageObjectsAsync(0)).Count;
        var newText = "Değiştirilmiş paragraf: Şirket, çalışanlarına ücretli izin hakkı tanır. " +
                      "Bu cümle kutu genişliğinden uzun olduğu için birden fazla satıra akmalıdır; ığüşöç İĞÜŞÖÇ.";
        var area = await doc.ReplaceParagraphAsync(0, p1, newText, Style());
        Assert.True(area.Left >= p1.Bounds.Left - 1 && area.Right <= p1.Bounds.Right + 2, $"Taşma: {area} / {p1.Bounds}");
        doc = await ReopenAsync(doc);
        using (doc)
        {
            var text = (await doc.GetPageTextAsync(0)).Text;
            Assert.DoesNotContain("Bu metin birinci", text);
            Assert.DoesNotContain("sona erer", text);
            var flat = text.Replace("\r\n", " ").Replace("\n", " ");
            Assert.Contains("ücretli izin hakkı", flat);
            Assert.Contains("ığüşöç İĞÜŞÖÇ", flat);
            var p = (await doc.GetParagraphsAsync(0)).Single(x => x.Text.StartsWith("Değiştirilmiş"));
            Assert.True(p.ObjectIndices.Count >= 2, "Metin birden fazla satıra akmalı");
            Assert.Equal(newText, p.Text);
            // Diğer içerik korunur
            Assert.Contains("Sözleşme Başlığı", text);
            Assert.Contains("İkinci paragraf", text);
            var objs = await doc.GetPageObjectsAsync(0);
            Assert.Contains(objs, o => o.Kind == PageObjectKind.Image);
            Assert.True(objs.Count >= before - 3 + 2);
        }
    }

    [Fact]
    public async Task ReplacedTextIsSearchableAndNotOverlay()
    {
        var doc = await PdfDocument.OpenAsync(Fx.F("edit_paragraphs.pdf"));
        var p2 = (await doc.GetParagraphsAsync(0)).Single(p => p.Text.StartsWith("İkinci"));
        await doc.ReplaceParagraphAsync(0, p2, "Yeni ikinci satır", Style());
        doc = await ReopenAsync(doc);
        using (doc)
        {
            // Üstüne beyaz kutu çizilmemiş olmalı: yeni dolgulu beyaz yol nesnesi yok.
            var objs = await doc.GetPageObjectsAsync(0);
            Assert.DoesNotContain(objs, o => o.Kind == PageObjectKind.Path && o.FillRgb == 0xFFFFFF);
            var hits = await doc.SearchAsync("Yeni ikinci", new SearchOptions());
            Assert.Single(hits);
            Assert.Empty(await doc.SearchAsync("tek satırdan", new SearchOptions()));
        }
    }

    [Fact]
    public async Task AddTextBlockAlignmentAndUnderline()
    {
        var doc = await PdfDocument.CreateAsync(new SizeF(595, 842), 1);
        var r1 = await doc.AddTextBlockAsync(0, 100, 700, 300, "Ortalı", Style(14, TextAlign.Center));
        var r2 = await doc.AddTextBlockAsync(0, 100, 600, 300, "Sağa", Style(14, TextAlign.Right, underline: true));
        Assert.Equal(250, (r1.Left + r1.Right) / 2, 0);
        Assert.Equal(400, r2.Right, 0);
        doc = await ReopenAsync(doc);
        using (doc)
        {
            var objs = await doc.GetPageObjectsAsync(0);
            Assert.Equal(2, objs.Count(o => o.Kind == PageObjectKind.Text));
            var ul = Assert.Single(objs, o => o.Kind == PageObjectKind.Path);
            Assert.Equal(PdfDocument.MarkUnderline, ul.MeslyRole);
        }
    }

    [Fact]
    public async Task MoveResizeDeleteAndAlignObjects()
    {
        var doc = await PdfDocument.OpenAsync(Fx.F("edit_paragraphs.pdf"));
        var objs = await doc.GetPageObjectsAsync(0);
        var img = objs.First(o => o.Kind == PageObjectKind.Image);
        var rect = objs.First(o => o.Kind == PageObjectKind.Path && o.FillRgb == 0x3366CC);
        await doc.MoveObjectsAsync(0, new[] { img.Index }, -50, 100);
        await doc.ResizeObjectAsync(0, rect.Index, new RectF(72, 400, 312, 460));
        doc = await ReopenAsync(doc);
        objs = await doc.GetPageObjectsAsync(0);
        var img2 = objs.First(o => o.Kind == PageObjectKind.Image);
        Assert.Equal(img.Bounds.Left - 50, img2.Bounds.Left, 1);
        Assert.Equal(img.Bounds.Bottom + 100, img2.Bounds.Bottom, 1);
        var rect2 = objs.First(o => o.Kind == PageObjectKind.Path && o.FillRgb == 0x3366CC);
        Assert.Equal(240, rect2.Bounds.Width, 1);
        Assert.Equal(400, rect2.Bounds.Bottom, 1);

        await doc.AlignObjectsAsync(0, new[] { img2.Index, rect2.Index }, AlignMode.Left);
        objs = await doc.GetPageObjectsAsync(0);
        Assert.Equal(objs[img2.Index].Bounds.Left, objs[rect2.Index].Bounds.Left, 1);

        await doc.DeleteObjectsAsync(0, new[] { img2.Index });
        doc = await ReopenAsync(doc);
        using (doc) Assert.DoesNotContain(await doc.GetPageObjectsAsync(0), o => o.Kind == PageObjectKind.Image);
    }

    [Fact]
    public async Task ColorChangeAndShapes()
    {
        var doc = await PdfDocument.CreateAsync(new SizeF(595, 842), 1);
        await doc.AddShapeAsync(0, ShapeKind.Ellipse, new RectF(100, 100, 300, 200), new ShapeStyle(0xFF0000, 2, 0x00FF00));
        await doc.AddShapeAsync(0, ShapeKind.Arrow, new RectF(100, 300, 300, 400), new ShapeStyle(0x0000FF, 2));
        await doc.AddTextBlockAsync(0, 72, 780, 300, "Renk", Style());
        var t = (await doc.GetPageObjectsAsync(0)).First(o => o.Kind == PageObjectKind.Text);
        await doc.SetFillColorAsync(0, new[] { t.Index }, 0xCC0000);
        doc = await ReopenAsync(doc);
        using (doc)
        {
            var objs = await doc.GetPageObjectsAsync(0);
            var e = objs.First(o => o.Kind == PageObjectKind.Path && o.FillRgb == 0x00FF00);
            Assert.InRange(e.Bounds.Width, 200, 206); // sınırlar çizgi kalınlığını içerir
            Assert.Equal(0xFF0000u, e.StrokeRgb);
            Assert.Contains(objs, o => o.Kind == PageObjectKind.Path && o.StrokeRgb == 0x0000FF);
            Assert.Equal(0xCC0000u, objs.First(o => o.Kind == PageObjectKind.Text).FillRgb);
        }
    }

    [Fact]
    public async Task AddAndReplaceImage()
    {
        var doc = await PdfDocument.CreateAsync(new SizeF(595, 842), 1);
        var jpg = new JpegImageSource(File.ReadAllBytes(Fx.F("photo.jpg")));
        var placed = await doc.AddImageAsync(0, jpg, new RectF(100, 100, 300, 500));
        Assert.True(placed.Width <= 200.01 && placed.Height <= 400.01);
        var img = (await doc.GetPageObjectsAsync(0)).Single(o => o.Kind == PageObjectKind.Image);
        var bgra = new BgraImage { Width = 40, Height = 20, Stride = 160, Pixels = new byte[40 * 20 * 4].Select((_, k) => (byte)(k % 4 == 3 ? 255 : 0)).ToArray() };
        await doc.ReplaceImageAsync(0, img.Index, new BitmapImageSource(bgra));
        doc = await ReopenAsync(doc);
        using (doc)
        {
            var img2 = (await doc.GetPageObjectsAsync(0)).Single(o => o.Kind == PageObjectKind.Image);
            Assert.Equal(2.0, img2.Bounds.Width / img2.Bounds.Height, 2);
            Assert.True(placed.Contains((img2.Bounds.Left + img2.Bounds.Right) / 2, (img2.Bounds.Bottom + img2.Bounds.Top) / 2));
        }
    }

    [Fact]
    public async Task LinksAddAndRemove()
    {
        var doc = await PdfDocument.CreateAsync(new SizeF(595, 842), 1);
        await Assert.ThrowsAsync<PdfException>(() => doc.AddLinkAsync(0, new RectF(0, 0, 10, 10), "javascript:alert(1)"));
        await doc.AddLinkAsync(0, new RectF(100, 100, 200, 130), "https://example.com/ğ");
        doc = await ReopenAsync(doc);
        var links = await doc.GetLinksAsync(0);
        var l = Assert.Single(links);
        Assert.StartsWith("https://example.com/", l.Uri);
        Assert.False(await doc.RemoveLinkAtAsync(0, 10, 10));
        Assert.True(await doc.RemoveLinkAtAsync(0, 150, 115));
        doc = await ReopenAsync(doc);
        using (doc) Assert.Empty(await doc.GetLinksAsync(0));
    }

    [Fact]
    public async Task WatermarkAddRemoveOnRotatedPage()
    {
        var doc = await Fx.CreateNumberedAsync(3);
        await doc.RotatePagesAsync(new[] { 1 }, 90);
        await doc.AddWatermarkAsync(new[] { 0, 1, 2 }, new WatermarkSettings("GİZLİ", Style(0)));
        doc = await ReopenAsync(doc);
        for (int i = 0; i < 3; i++)
        {
            var wm = Assert.Single(await doc.GetPageObjectsAsync(i), o => o.MeslyRole == PdfDocument.MarkWatermark);
            // Filigran sayfanın ortasında (kutu merkezi ≈ sayfa merkezi).
            double cx = (wm.Bounds.Left + wm.Bounds.Right) / 2, cy = (wm.Bounds.Bottom + wm.Bounds.Top) / 2;
            Assert.InRange(cx, 297.5 - 30, 297.5 + 30);
            Assert.InRange(cy, 421 - 30, 421 + 30);
            Assert.Contains("GİZLİ", wm.Text);
        }
        // Filigran paragraf listesinde çıkmaz.
        Assert.DoesNotContain(await doc.GetParagraphsAsync(0), p => p.Text.Contains("GİZLİ"));
        Assert.Equal(3, await doc.RemoveWatermarksAsync(new[] { 0, 1, 2 }));
        doc = await ReopenAsync(doc);
        using (doc)
        {
            Assert.DoesNotContain("GİZLİ", (await doc.GetPageTextAsync(0)).Text);
            Assert.Equal("Sayfa 1", await Fx.FirstLineAsync(doc, 0));
        }
    }

    [Fact]
    public async Task HeaderFooterPageNumbers()
    {
        var doc = await Fx.CreateNumberedAsync(4);
        var s = new HeaderFooterSettings(new[]
        {
            new HeaderFooterItem(HFPosition.BottomCenter, "Sayfa {sayfa} / {toplam}"),
            new HeaderFooterItem(HFPosition.TopRight, "{dosya}"),
        }, Style(9), FileName: "rapor.pdf");
        await doc.AddHeaderFooterAsync(new[] { 1, 2, 3 }, s);
        doc = await ReopenAsync(doc);
        var t2 = (await doc.GetPageTextAsync(2)).Text;
        Assert.Contains("Sayfa 2 / 3", t2);
        Assert.Contains("rapor.pdf", t2);
        Assert.DoesNotContain("rapor.pdf", (await doc.GetPageTextAsync(0)).Text);
        var foot = (await doc.GetPageObjectsAsync(3)).First(o => o.MeslyRole == PdfDocument.MarkHeaderFooter && o.Text!.StartsWith("Sayfa 3"));
        Assert.InRange(foot.Bounds.Bottom, 20, 40);
        Assert.Equal(297.5, (foot.Bounds.Left + foot.Bounds.Right) / 2, 0);
        Assert.Equal(6, await doc.RemoveHeaderFooterAsync(Enumerable.Range(0, 4)));
        using (doc) Assert.DoesNotContain("rapor.pdf", (await doc.GetPageTextAsync(2)).Text);
    }

    [Fact]
    public async Task BackgroundIsBehindContent()
    {
        var doc = await Fx.CreateNumberedAsync(1);
        await doc.SetBackgroundAsync(new[] { 0 }, 0xFFF3C0);
        await doc.SetBackgroundAsync(new[] { 0 }, 0xE0F0FF); // ikinci kez: eskisinin yerine geçer
        doc = await ReopenAsync(doc);
        using (doc)
        {
            var objs = await doc.GetPageObjectsAsync(0);
            Assert.Equal(PdfDocument.MarkBackground, objs[0].MeslyRole);
            Assert.Single(objs, o => o.MeslyRole == PdfDocument.MarkBackground);
            Assert.Equal(0xE0F0FFu, objs[0].FillRgb);
            var img = await doc.RenderAsync(0, new RenderRequest(300, 424));
            // Köşe pikseli arka plan rengi (BGRA)
            Assert.Equal(new byte[] { 0xFF, 0xF0, 0xE0 }, img.Pixels[..3]);
            Assert.Equal("Sayfa 1", await Fx.FirstLineAsync(doc, 0));
        }
    }

    [Fact]
    public async Task EditingIsUndoableViaSnapshot()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("edit_paragraphs.pdf"));
        var snap = await doc.CreateSnapshotAsync(Fx.TempDir());
        var p = (await doc.GetParagraphsAsync(0)).First();
        await doc.ReplaceParagraphAsync(0, p, "Geçici", Style());
        Assert.Contains("Geçici", (await doc.GetPageTextAsync(0)).Text);
        await doc.RestoreSnapshotAsync(snap);
        Assert.DoesNotContain("Geçici", (await doc.GetPageTextAsync(0)).Text);
        Assert.Contains("Sözleşme Başlığı", (await doc.GetPageTextAsync(0)).Text);
    }

    [Fact]
    public void WrapAndTemplate()
    {
        var lines = TextLayout.Wrap("aa bb cc dd\n\nee", 5, s => s.Length);
        Assert.Equal(new[] { "aa bb", "cc dd", "", "ee" }, lines);
        Assert.Equal("3/9 x.pdf 01.02.2026", TextLayout.Fill("{sayfa}/{toplam} {dosya} {tarih}", 3, 9, "x.pdf", new DateTime(2026, 2, 1)));
        Assert.Equal(("Arial", true, false), TextLayout.ParseFontName("ABCDEF+Arial-BoldMT"));
    }
}
