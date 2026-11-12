using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Xunit;

namespace MeslyPdfReader.Tests;

/// <summary>
/// Metin düzenleme sadakati: düzenlenen metin yerinde kalmalı (köken, taban çizgisi, hizalama, girinti, satır kırılımı),
/// sayfadaki başka hiçbir şey kıpırdamamalı (iki yana yaslı Tw/Tc metinleri dahil) ve mümkünse özgün yazı tipi korunmalı.
/// </summary>
public class EditFidelityTests
{
    private static string Norm(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static TextStyle Same(TextParagraph p, bool original = true) =>
        new(Fx.Font, p.FontSize, p.Rgb, p.Align, false, p.LineHeight / p.FontSize, 255, original);

    private static async Task<TextParagraph> Find(PdfDocument d, string start) =>
        (await d.GetParagraphsAsync(0)).Single(x => x.Text.StartsWith(start, StringComparison.Ordinal));

    /// <summary>Paragraf alanı (+pay) dışında değişen piksel sayısı.</summary>
    private static int ChangedOutside(BgraImage a, BgraImage b, RectF area, double pageH, double dpi, double pad = 3)
    {
        double k = dpi / 72, x0 = (area.Left - pad) * k, x1 = (area.Right + pad) * k, y0 = (pageH - area.Top - pad) * k, y1 = (pageH - area.Bottom + pad) * k;
        int n = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                if (x >= x0 && x <= x1 && y >= y0 && y <= y1) continue;
                int o = y * a.Stride + x * 4;
                if (Math.Abs(a.Pixels[o] - b.Pixels[o]) + Math.Abs(a.Pixels[o + 1] - b.Pixels[o + 1]) + Math.Abs(a.Pixels[o + 2] - b.Pixels[o + 2]) > 30) n++;
            }
        return n;
    }

    [Theory]
    [InlineData("convert_sample.pdf")] // reportlab: Tw ile iki yana yaslı paragraflar, tablo hücreleri
    [InlineData("edit_word.pdf")]      // LibreOffice (Word): başlık, girinti, ortalı, sağa yaslı
    [InlineData("edit_chrome.pdf")]    // Chrome/Skia: ters y ekseni, ölçekli CTM, döndürülmüş etiket
    public async Task EditingOneParagraphLeavesEverythingElseUntouched(string file)
    {
        List<string> keys;
        using (var d0 = await PdfDocument.OpenAsync(Fx.F(file))) keys = (await d0.GetParagraphsAsync(0)).Select(p => p.Text).ToList();
        Assert.True(keys.Count >= 3);
        foreach (var key in keys)
        {
            using var d = await PdfDocument.OpenAsync(Fx.F(file));
            var p = (await d.GetParagraphsAsync(0)).First(x => x.Text == key);
            var before = await PdfTools.RenderAtDpiAsync(d, 0, 72);
            await d.ReplaceParagraphAsync(0, p, p.Text, Same(p));
            var after = await PdfTools.RenderAtDpiAsync(d, 0, 72);
            // Yedek yazı tipi daha genişse yeni metin özgün alanın biraz dışına taşabilir: iki alanın birleşimi.
            var now = (await d.GetParagraphsAsync(0)).First(x => Norm(x.Text) == Norm(key));
            var area = p.Bounds.Union(now.Bounds);
            int changed = ChangedOutside(before, after, area, d.GetPageSize(0).Height, 72);
            Assert.True(changed == 0, $"{file} / “{key[..Math.Min(30, key.Length)]}”: paragraf dışında {changed} piksel değişti");
        }
    }

    [Theory]
    [InlineData("convert_sample.pdf")]
    [InlineData("edit_word.pdf")]
    [InlineData("edit_chrome.pdf")]
    public async Task RepeatedEditsDoNotDrift(string file)
    {
        using var d = await PdfDocument.OpenAsync(Fx.F(file));
        var orig = (await d.GetParagraphsAsync(0)).ToList();
        foreach (var o in orig)
        {
            for (int k = 0; k < 4; k++)
            {
                var cur = (await d.GetParagraphsAsync(0)).Single(x => Norm(x.Text) == Norm(o.Text));
                await d.ReplaceParagraphAsync(0, cur, cur.Text, Same(cur, original: k % 2 == 0)); // özgün ve yedek yazı tipi dönüşümlü
            }
            var fin = (await d.GetParagraphsAsync(0)).Single(x => Norm(x.Text) == Norm(o.Text));
            string what = $"{file} / “{o.Text[..Math.Min(30, o.Text.Length)]}”";
            // Hizalamanın çıpası korunur: sola/iki yana yaslıda başlangıç, ortalıda merkez, sağa yaslıda sağ kenar.
            double Anchor(TextParagraph t) => o.Align switch
            {
                TextAlign.Center => (t.Lines![0].X + t.Lines[0].Right) / 2,
                TextAlign.Right => t.Lines![0].Right,
                _ => t.Lines![0].X,
            };
            Assert.True(Math.Abs(Anchor(fin) - Anchor(o)) < 0.6, $"{what}: {o.Align} çıpası {Anchor(o):0.00} → {Anchor(fin):0.00}");
            Assert.True(Math.Abs(fin.FirstBaseline - o.FirstBaseline) < 0.05, $"{what}: taban {o.FirstBaseline:0.00} → {fin.FirstBaseline:0.00}");
            Assert.True(Math.Abs(fin.Lines[^1].Baseline - o.Lines[^1].Baseline) < 0.05, $"{what}: son satır kaydı");
            Assert.True(fin.Lines.Count == o.Lines.Count, $"{what}: satır sayısı {o.Lines.Count} → {fin.Lines.Count}");
            Assert.True(fin.Align == o.Align, $"{what}: hizalama {o.Align} → {fin.Align}");
            Assert.True(Math.Abs(fin.Angle - o.Angle) < 0.1, $"{what}: açı {o.Angle} → {fin.Angle}");
        }
    }

    [Fact]
    public async Task DetectsAlignmentIndentAndJustification()
    {
        using var d = await PdfDocument.OpenAsync(Fx.F("edit_word.pdf"));
        Assert.Equal(TextAlign.Justify, (await Find(d, "İki yana yaslı")).Align);
        Assert.Equal(TextAlign.Center, (await Find(d, "Ortalanmış satır")).Align);
        Assert.Equal(TextAlign.Right, (await Find(d, "Sağa yaslı")).Align);
        var ind = await Find(d, "İlk satırı girintili");
        Assert.True(ind.Lines![0].X - ind.Lines[1].X > 30, "ilk satır girintisi algılanmadı");
        using var s = await PdfDocument.OpenAsync(Fx.F("convert_sample.pdf"));
        // Boş satır olmadan art arda gelen iki paragraf ayrı paragraflar olarak okunur.
        Assert.Equal(TextAlign.Justify, (await Find(s, "Bu rapor")).Align);
        Assert.Equal(TextAlign.Justify, (await Find(s, "İkinci paragraf")).Align);
    }

    [Fact]
    public async Task UnrelatedJustifiedTextKeepsItsWordSpacing()
    {
        // Sayfa içeriği yeniden yazılınca PDFium Tw'yi atar; konum koruması olmazsa iki yana yaslı satırlar daralır.
        using var d = await PdfDocument.OpenAsync(Fx.F("convert_sample.pdf"));
        var target = await Find(d, "İkinci paragraf");
        var title = await Find(d, "Hizmet Raporu");
        await d.ReplaceParagraphAsync(0, title, "Hizmet Raporu 2027", Same(title));
        var after = await Find(d, "İkinci paragraf");
        Assert.Equal(target.Lines![0].Right, after.Lines![0].Right, 1);
        // Kaydedip yeniden açınca da aynı.
        var path = Path.Combine(Fx.TempDir(), "kayit.pdf");
        await d.SaveCopyAsync(path);
        using var r = await PdfDocument.OpenAsync(path);
        Assert.Equal(target.Lines[0].Right, (await Find(r, "İkinci paragraf")).Lines![0].Right, 1);
        Assert.Equal(TextAlign.Center, (await Find(r, "Hizmet Raporu 2027")).Align);
    }

    [Fact]
    public async Task TableCellGrowsSidewaysWithoutWrapping()
    {
        using var d = await PdfDocument.OpenAsync(Fx.F("convert_sample.pdf"));
        var cell = await Find(d, "1.250");
        await d.ReplaceParagraphAsync(0, cell, "1.250 adet", Same(cell));
        var now = await Find(d, "1.250 adet");
        Assert.Single(now.Lines!);
        Assert.Equal(cell.Lines![0].X, now.Lines![0].X, 1);
        Assert.Equal(cell.FirstBaseline, now.FirstBaseline, 2);
        // Yandaki hücre yerinde ve ayrı kalır.
        Assert.Contains(await d.GetParagraphsAsync(0), p => p.Text == "3.437,50");
    }

    [Fact]
    public async Task CenteredAndRightAlignedLinesKeepTheirAnchor()
    {
        using var d = await PdfDocument.OpenAsync(Fx.F("edit_word.pdf"));
        var c = await Find(d, "Ortalanmış satır");
        double center = (c.Bounds.Left + c.Bounds.Right) / 2;
        await d.ReplaceParagraphAsync(0, c, "Ek-2", Same(c));
        var c2 = await Find(d, "Ek-2");
        Assert.True(Math.Abs((c2.Bounds.Left + c2.Bounds.Right) / 2 - center) < 1, "ortalanmış satır merkezini kaybetti");
        var r = await Find(d, "Sağa yaslı");
        await d.ReplaceParagraphAsync(0, r, "Sağa yaslı: 1.1.2027 Pazartesi", Same(r));
        var r2 = await Find(d, "Sağa yaslı: 1.1.2027");
        Assert.True(Math.Abs(r2.Bounds.Right - r.Bounds.Right) < 1, $"sağ kenar {r.Bounds.Right:0.0} → {r2.Bounds.Right:0.0}");
    }

    [Fact]
    public async Task EditedJustifiedParagraphStaysJustifiedAndIndentStays()
    {
        using var d = await PdfDocument.OpenAsync(Fx.F("edit_word.pdf"));
        var j = await Find(d, "İki yana yaslı");
        await d.ReplaceParagraphAsync(0, j, j.Text.Replace("uyuşmazlıklarda", "anlaşmazlıklarda"), Same(j));
        var j2 = await Find(d, "İki yana yaslı");
        Assert.Equal(TextAlign.Justify, j2.Align);
        Assert.Equal(j.Lines![0].X, j2.Lines![0].X, 1);
        var rights = j2.Lines!.Take(j2.Lines.Count - 1).Select(l => l.Right).ToList();
        Assert.True(rights.Max() - rights.Min() < 0.6, "iki yana yaslı satırların sağ kenarları hizalı değil");
        var ind = await Find(d, "İlk satırı girintili");
        double indent = ind.Lines![0].X - ind.Lines[1].X;
        await d.ReplaceParagraphAsync(0, ind, ind.Text + " Ek madde.", Same(ind));
        var ind2 = await Find(d, "İlk satırı girintili");
        Assert.Equal(indent, ind2.Lines![0].X - ind2.Lines[1].X, 1);
        Assert.Equal(ind.FirstBaseline, ind2.FirstBaseline, 2);
    }

    [Fact]
    public async Task UsesOriginalEmbeddedFontAndFallsBackWhenGlyphMissing()
    {
        using var d = await PdfDocument.OpenAsync(Fx.F("convert_sample.pdf"));
        var cell = await Find(d, "Defter");
        await d.ReplaceParagraphAsync(0, cell, "Defte", Same(cell));
        Assert.True(d.LastUsedOriginalFont, "karakterleri gömülü yazı tipinde olan metinde özgün yazı tipi kullanılmalı");
        var cell2 = await Find(d, "Defte");
        Assert.Equal("Defte", cell2.Text);
        // Alt kümede bulunmayan karakter (Ω): sistem yazı tipine geçilir, metin doğru okunur.
        await d.ReplaceParagraphAsync(0, cell2, "Ω Defter", Same(cell2));
        Assert.False(d.LastUsedOriginalFont);
        Assert.Contains(await d.GetParagraphsAsync(0), p => p.Text == "Ω Defter");
    }

    [Fact]
    public async Task RotatedTextKeepsAngleAndOrigin()
    {
        using var d = await PdfDocument.OpenAsync(Fx.F("edit_chrome.pdf"));
        var r = await Find(d, "Döndürülmüş etiket");
        Assert.Equal(90, r.Angle, 1);
        await d.ReplaceParagraphAsync(0, r, "Döndürülmüş etiket: Arşiv No 43", Same(r));
        var r2 = await Find(d, "Döndürülmüş etiket: Arşiv No 43");
        Assert.Equal(90, r2.Angle, 1);
        Assert.Equal(r.Lines![0].X, r2.Lines![0].X, 1);
        Assert.Equal(r.Lines[0].Baseline, r2.Lines![0].Baseline, 1);
    }

    [Fact]
    public void WrapExKeepsFirstLineIndentAndMarksParagraphEnds()
    {
        double M(string s) => s.Length * 5;
        var lines = TextLayout.WrapEx("aa bb cc dd ee\nff", 20, 30, M);
        Assert.Equal(new[] { "aa", "bb cc", "dd ee", "ff" }, lines.Select(l => l.Text));
        Assert.Equal(new[] { false, false, true, true }, lines.Select(l => l.EndsParagraph));
    }
}
