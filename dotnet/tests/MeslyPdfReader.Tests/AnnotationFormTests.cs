using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using MeslyPdfReader.Convert;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Xunit;
using Xunit.Abstractions;
using A = DocumentFormat.OpenXml.Drawing;

namespace MeslyPdfReader.Tests;

/// <summary>0.7.0: açıklamalar (vurgu, altı/üstü çizili, not), form doldurma, düzleştirme ve PDF → PowerPoint.</summary>
public class AnnotationFormTests(ITestOutputHelper log)
{
    private static async Task<(PdfDocument Doc, PageText Text)> SampleAsync()
    {
        var doc = await Fx.CreateNumberedAsync(2, "İşaretlenecek satır: Çanakkale Boğazı");
        var t = await doc.GetPageTextAsync(0);
        return (doc, t);
    }

    /// <summary>Sayfanın verilen dikdörtgen içindeki ortalama rengi (RGB).</summary>
    private static async Task<(double R, double G, double B)> AvgColor(PdfDocument doc, int page, RectF r)
    {
        const double scale = 2;
        var size = doc.GetPageSize(page);
        int w = (int)(size.Width * scale), h = (int)(size.Height * scale);
        var img = await doc.RenderAsync(page, new RenderRequest(w, h, 0, 0, w, h, 0, Annotations: true));
        int x0 = (int)(r.Left * scale), x1 = (int)(r.Right * scale), y0 = (int)((size.Height - r.Top) * scale), y1 = (int)((size.Height - r.Bottom) * scale);
        double sr = 0, sg = 0, sb = 0; int n = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
            {
                int o = y * img.Stride + x * 4;
                sb += img.Pixels[o]; sg += img.Pixels[o + 1]; sr += img.Pixels[o + 2]; n++;
            }
        return n == 0 ? (0, 0, 0) : (sr / n, sg / n, sb / n);
    }

    [Fact]
    public async Task HighlightIsRealAnnotationRenderedAndSaved()
    {
        var (doc, t) = await SampleAsync();
        using var _ = doc;
        int start = t.Text.IndexOf("Çanakkale", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var rects = t.GetLineRects(start, start + "Çanakkale Boğazı".Length - 1);
        var before = await AvgColor(doc, 0, rects[0]);
        await doc.AddMarkupAsync(0, AnnotKind.Highlight, rects, 0xFFE14D, "Önemli");
        var after = await AvgColor(doc, 0, rects[0]);
        log.WriteLine($"önce {before} sonra {after}");
        // Sarı vurgu: mavi kanal belirgin düşer, kırmızı yüksek kalır.
        Assert.True(after.B < before.B - 40, $"vurgu görünmüyor: {before} → {after}");
        Assert.True(after.R > 150);

        var list = await doc.GetAnnotationsAsync(0);
        var a = Assert.Single(list);
        Assert.Equal(AnnotKind.Highlight, a.Kind);
        Assert.Equal("Önemli", a.Contents);
        Assert.Equal(0xFFE14Du, a.Rgb);

        // Metin değişmemeli (vurgu içerik akışına yazılmaz)
        Assert.Contains("Çanakkale Boğazı", (await doc.GetPageTextAsync(0)).Text);

        var path = Path.Combine(Fx.TempDir(), "vurgu.pdf");
        await doc.SaveAsync(path);
        using var re = await PdfDocument.OpenAsync(path);
        var reList = await re.GetAnnotationsAsync(0);
        Assert.Equal(AnnotKind.Highlight, Assert.Single(reList).Kind);
        var again = await AvgColor(re, 0, rects[0]);
        Assert.True(again.B < before.B - 40, "kaydedilen dosyada vurgu görünmüyor");
    }

    [Fact]
    public async Task UnderlineStrikeNoteListAndRemove()
    {
        var (doc, t) = await SampleAsync();
        using var _ = doc;
        int start = t.Text.IndexOf("İşaretlenecek", StringComparison.Ordinal);
        var rects = t.GetLineRects(start, start + 12);
        await doc.AddMarkupAsync(0, AnnotKind.Underline, rects, 0xE53935);
        await doc.AddMarkupAsync(0, AnnotKind.StrikeOut, rects, 0x5FB4FF);
        await doc.AddNoteAsync(1, 300, 500, "Madde güncellendi.", 0xFFE14D);
        var all = await doc.GetAllAnnotationsAsync();
        Assert.Equal(3, all.Count);
        Assert.Contains(all, x => x.Kind == AnnotKind.Underline && x.Page == 0);
        Assert.Contains(all, x => x.Kind == AnnotKind.StrikeOut && x.Page == 0);
        var note = Assert.Single(all, x => x.Kind == AnnotKind.Note);
        Assert.Equal(1, note.Page);
        Assert.Equal("Madde güncellendi.", note.Contents);
        // Not simgesi çizilmeli (beyaz olmayan pikseller)
        var c = await AvgColor(doc, 1, note.Rect);
        Assert.True(c.R + c.G + c.B < 740, $"not simgesi görünmüyor: {c}");

        Assert.True(await doc.RemoveAnnotationAsync(all.Single(x => x.Kind == AnnotKind.StrikeOut)));
        Assert.Equal(2, (await doc.GetAllAnnotationsAsync()).Count);
        Assert.Equal(1, await doc.RemovePageAnnotationsAsync(0));
        Assert.Single(await doc.GetAllAnnotationsAsync());
    }

    [Fact]
    public async Task FillsFormFieldsAndPersists()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("form.pdf"));
        var fields = await doc.GetFormFieldsAsync();
        foreach (var f in fields) log.WriteLine($"{f.Name} {f.Kind} '{f.Value}' chk={f.Checked} exp={f.ExportValue} opts={string.Join("|", f.Options)} sel={f.SelectedOption}");
        var name = Assert.Single(fields, f => f.Name == "adsoyad");
        Assert.Equal(FormFieldKind.Text, name.Kind);
        var chk = Assert.Single(fields, f => f.Name == "onay");
        Assert.Equal(FormFieldKind.CheckBox, chk.Kind);
        Assert.False(chk.Checked);
        var city = Assert.Single(fields, f => f.Name == "sehir");
        Assert.Equal(FormFieldKind.ComboBox, city.Kind);
        Assert.Equal(new[] { "Ankara", "Istanbul", "Izmir" }, city.Options);
        var blank = await AvgColor(doc, 0, name.Rect);
        Assert.True(blank.R < 250, $"boş form alanının çerçevesi çizilmiyor (FFLDraw): {blank}");
        var radios = fields.Where(f => f.Name == "secim").ToList();
        Assert.Equal(2, radios.Count);
        var evet = radios.Single(r => r.ExportValue == "evet");
        Assert.False(evet.Checked);

        int n = await doc.SetFormFieldsAsync(new[]
        {
            new FormFieldChange(name, Text: "Ayşe Yılmaz"),
            new FormFieldChange(chk, Checked: true),
            new FormFieldChange(city, Option: 2),
            new FormFieldChange(evet, Checked: true),
        });
        Assert.Equal(4, n);
        Assert.True(doc.IsModified);

        var path = Path.Combine(Fx.TempDir(), "form_dolu.pdf");
        await doc.SaveAsync(path);
        using var re = await PdfDocument.OpenAsync(path);
        var after = await re.GetFormFieldsAsync();
        Assert.Equal("Ayşe Yılmaz", after.Single(f => f.Name == "adsoyad").Value);
        Assert.True(after.Single(f => f.Name == "onay").Checked);
        Assert.Equal("Izmir", after.Single(f => f.Name == "sehir").Value);
        Assert.True(after.Single(f => f.Name == "secim" && f.ExportValue == "evet").Checked);
        Assert.False(after.Single(f => f.Name == "secim" && f.ExportValue == "hayir").Checked);

        // Görünüm güncellenmiş olmalı: alan içinde koyu (metin) pikseller
        var r = after.Single(f => f.Name == "adsoyad").Rect;
        var c = await AvgColor(re, 0, r);
        Assert.True(c.R < blank.R - 3, $"doldurulan metin çizilmemiş: boş {blank}, dolu {c}");

        // Sıfırla: metin boş, onay kalkar
        int reset = await re.SetFormFieldsAsync(new[]
        {
            new FormFieldChange(after.Single(f => f.Name == "adsoyad"), Text: ""),
            new FormFieldChange(after.Single(f => f.Name == "onay"), Checked: false),
        });
        Assert.Equal(2, reset);
        var cleared = await re.GetFormFieldsAsync();
        Assert.Equal("", cleared.Single(f => f.Name == "adsoyad").Value);
        Assert.False(cleared.Single(f => f.Name == "onay").Checked);
    }

    [Fact]
    public async Task FlattenBakesFieldsAndAnnotations()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("form.pdf"));
        var fields = await doc.GetFormFieldsAsync();
        await doc.SetFormFieldsAsync(new[] { new FormFieldChange(fields.Single(f => f.Name == "adsoyad"), Text: "Düzleştirilecek") });
        var t = await doc.GetPageTextAsync(0);
        int s = t.Text.IndexOf("Onay", StringComparison.Ordinal);
        await doc.AddMarkupAsync(0, AnnotKind.Highlight, t.GetLineRects(s, s + 3), 0xFFE14D);
        var r = fields.Single(f => f.Name == "adsoyad").Rect;
        var before = await AvgColor(doc, 0, r);
        Assert.Equal(1, await doc.FlattenAsync());
        Assert.Empty(await doc.GetFormFieldsAsync());
        Assert.Empty(await doc.GetAllAnnotationsAsync());
        // Doldurulan değer artık sayfa içeriği: metin olarak okunur ve aynı görünür.
        Assert.Contains("Düzleştirilecek", (await doc.GetPageTextAsync(0)).Text);
        var after = await AvgColor(doc, 0, r);
        Assert.InRange(after.R, before.R - 25, before.R + 25);
    }

    [Fact]
    public async Task WritesValidEditablePptx()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("convert_sample.pdf"));
        var path = Path.Combine(Fx.TempDir(), "sunu.pptx");
        var layouts = await PdfConverter.AnalyzeAsync(doc, Enumerable.Range(0, doc.PageCount).ToList());
        PptxWriter.Write(path, layouts, "Hizmet Raporu");
        using var p = PresentationDocument.Open(path, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2016).Validate(p).ToList();
        foreach (var e in errors.Take(20)) log.WriteLine($"{e.Path?.XPath}: {e.Description}");
        Assert.Empty(errors);
        var slides = p.PresentationPart!.SlideParts.ToList();
        Assert.Equal(doc.PageCount, slides.Count);
        var text = string.Join("\n", slides[0].Slide.Descendants<A.Text>().Select(x => x.Text));
        Assert.Contains("Hizmet Raporu 2026", text);
        Assert.Contains("ığüşöç İĞÜŞÖÇ", text);
        Assert.Contains("3.437,50", text);
        Assert.Single(slides[0].Slide.Descendants<A.Table>());
        Assert.NotEmpty(slides[0].ImageParts);
        // Slayt boyutu = sayfa boyutu
        var size = p.PresentationPart.Presentation.SlideSize!;
        Assert.InRange(size.Cx!.Value, (int)Math.Round(doc.GetPageSize(0).Width * 12700) - 2000, (int)Math.Round(doc.GetPageSize(0).Width * 12700) + 2000);
    }
}

public class TransparentImageTests
{
    [Fact]
    public async Task TransparentSignatureDoesNotCoverText()
    {
        using var doc = await Fx.CreateNumberedAsync(1, "İmzanın altındaki metin görünmeli");
        var t = await doc.GetPageTextAsync(0);
        int i = t.Text.IndexOf("altındaki", StringComparison.Ordinal);
        var line = t.GetLineRects(i, i + 8)[0];
        // Saydam zeminli, ortasında kırmızı çizgi olan "imza"
        int w = 200, h = 60;
        var px = new byte[w * h * 4];
        for (int y = 28; y < 32; y++) for (int x = 10; x < 190; x++) { int o = (y * w + x) * 4; px[o + 2] = 200; px[o + 3] = 255; }
        var img = new BgraImage { Width = w, Height = h, Stride = w * 4, Pixels = px };
        var area = new RectF(line.Left - 10, line.Bottom - 20, line.Left + 190, line.Top + 20);
        await doc.AddImageAsync(0, new BitmapImageSource(img) { Transparent = true }, area);
        // Metin hâlâ çizilmeli: satır kutusunun üst kısmında koyu pikseller kalır.
        var size = doc.GetPageSize(0);
        var r = await doc.RenderAsync(0, new RenderRequest((int)size.Width * 2, (int)size.Height * 2));
        int dark = 0;
        for (int y = (int)((size.Height - line.Top) * 2); y < (int)((size.Height - line.Top + line.Height * 0.4) * 2); y++)
            for (int x = (int)(line.Left * 2); x < (int)(line.Right * 2); x++)
            {
                int o = y * r.Stride + x * 4;
                if (r.Pixels[o] < 100 && r.Pixels[o + 1] < 100 && r.Pixels[o + 2] < 100) dark++;
            }
        Assert.True(dark > 20, $"imza altındaki metin örtülmüş ({dark} koyu piksel)");
        // Görselin saydam (boş) kısmı sayfa zemini gibi beyaz kalmalı; saydamlık yok sayılsaydı siyah olurdu.
        int yEmpty = (int)((size.Height - (area.Bottom + 4)) * 2), xEmpty = (int)((area.Left + 100) * 2);
        int oe = yEmpty * r.Stride + xEmpty * 4;
        Assert.True(r.Pixels[oe] > 240 && r.Pixels[oe + 1] > 240 && r.Pixels[oe + 2] > 240, $"saydam alan beyaz değil: {r.Pixels[oe]},{r.Pixels[oe + 1]},{r.Pixels[oe + 2]}");
        // Kırmızı çizgi çizilmiş olmalı
        int yLine = (int)((size.Height - (area.Top - 30 * area.Height / 60)) * 2), ol = yLine * r.Stride + xEmpty * 4;
        Assert.True(r.Pixels[ol + 2] > 150 && r.Pixels[ol] < 90, $"imza çizgisi yok: {r.Pixels[ol]},{r.Pixels[ol + 1]},{r.Pixels[ol + 2]}");
    }
}
