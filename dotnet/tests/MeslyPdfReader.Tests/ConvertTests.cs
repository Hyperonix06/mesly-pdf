using DocumentFormat.OpenXml.Packaging;
using MeslyPdfReader.Convert;
using MeslyPdfReader.Core;
using MeslyPdfReader.Ocr;
using MeslyPdfReader.Pdf;
using Xunit;
using Xunit.Abstractions;
using W = DocumentFormat.OpenXml.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace MeslyPdfReader.Tests;

/// <summary>Aşama 5: PDF → Word / Excel. Üretilen dosyalar OpenXML ile yeniden açılıp içerik denetlenir.</summary>
public class ConvertTests(ITestOutputHelper log)
{
    private static string Tmp(string name) => Path.Combine(Fx.TempDir(), name);

    [Fact]
    public async Task AnalyzesThirdPartyLayout()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("convert_sample.pdf"));
        var page = await LayoutAnalyzer.AnalyzeAsync(doc, 0);
        foreach (var b in page.Blocks) log.WriteLine($"{b.GetType().Name} {b.Box} {(b as ParagraphBlock)?.Text[..Math.Min(60, (b as ParagraphBlock)!.Text.Length)]}");
        Assert.False(page.FromOcr);
        var paras = page.Blocks.OfType<ParagraphBlock>().ToList();
        var title = paras[0];
        Assert.Equal("Hizmet Raporu 2026", title.Text);
        Assert.Equal(1, title.HeadingLevel);
        Assert.Equal(ParaAlign.Center, title.Align);
        Assert.True(title.Runs[0].Bold);
        var p1 = paras.Single(p => p.Text.StartsWith("Bu rapor"));
        Assert.EndsWith("ığüşöç İĞÜŞÖÇ.", p1.Text);
        Assert.Equal(ParaAlign.Justify, p1.Align);
        var p2 = Assert.Single(paras, p => p.Text.StartsWith("İkinci paragraf") && p.Text.EndsWith("yazılmalıdır."));
        Assert.Equal(ParaAlign.Justify, p2.Align); // iki satırlık iki yana yaslı paragraf

        var table = Assert.Single(page.Blocks.OfType<TableBlock>());
        Assert.Equal(4, table.Rows);
        Assert.Equal(3, table.Cols);
        var head = table.Cells.Single(c => c.Row == 0);
        Assert.Equal(3, head.ColSpan);
        Assert.Equal("Aylık Satışlar", head.Text);
        Assert.Equal("3.437,50", table.Cells.Single(c => c.Row == 2 && c.Col == 2).Text);
        Assert.Single(page.Blocks.OfType<ImageBlock>());

        // Okuma sırası: başlık → paragraflar → tablo → görsel → sol sütun → sağ sütun
        int Idx(Func<LayoutBlock, bool> f) => page.Blocks.ToList().FindIndex(b => f(b));
        int iTable = Idx(b => b is TableBlock), iImg = Idx(b => b is ImageBlock);
        int iLeft = Idx(b => b is ParagraphBlock p && p.Text.StartsWith("Sol sütun"));
        int iRight = Idx(b => b is ParagraphBlock p && p.Text.StartsWith("Sağ sütun"));
        Assert.True(Idx(b => b == p1) < iTable && iTable < iImg && iImg < iLeft && iLeft < iRight, $"{iTable} {iImg} {iLeft} {iRight}");
        // Sütunlar karışmamalı: sol sütun paragrafı sağ sütun metni içermez.
        Assert.DoesNotContain("Sağ sütun", ((ParagraphBlock)page.Blocks[iLeft]).Text);
    }

    [Fact]
    public async Task WritesDocxWithFormattingTableAndImage()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("convert_sample.pdf"));
        var path = Tmp("rapor.docx");
        await PdfConverter.ToDocxAsync(doc, path, new[] { 0 }, "Hizmet Raporu");
        using var w = WordprocessingDocument.Open(path, false);
        var body = w.MainDocumentPart!.Document.Body!;
        var paras = body.Elements<W.Paragraph>().ToList();
        var title = paras.First(p => p.InnerText == "Hizmet Raporu 2026");
        Assert.Equal("Heading1", title.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        Assert.Equal(W.JustificationValues.Center, title.ParagraphProperties?.Justification?.Val?.Value);
        Assert.Equal("40", title.Descendants<W.FontSize>().First().Val?.Value); // 20 pt
        var p1 = paras.First(p => p.InnerText.StartsWith("Bu rapor"));
        Assert.Equal(W.JustificationValues.Both, p1.ParagraphProperties?.Justification?.Val?.Value);
        Assert.Contains("ığüşöç İĞÜŞÖÇ", p1.InnerText);
        Assert.Contains("DejaVu Sans", p1.Descendants<W.RunFonts>().First().Ascii?.Value);

        var table = Assert.Single(body.Elements<W.Table>());
        var rows = table.Elements<W.TableRow>().ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal(3, rows[0].Elements<W.TableCell>().Single().TableCellProperties?.GridSpan?.Val?.Value);
        Assert.Equal(new[] { "Kalem", "1.250", "3.437,50" }, rows[2].Elements<W.TableCell>().Select(c => c.InnerText).ToArray());
        Assert.Single(w.MainDocumentPart.ImageParts);
        var size = body.Elements<W.SectionProperties>().Single().GetFirstChild<W.PageSize>()!;
        Assert.InRange((int)size.Width!.Value, 11890, 11920);   // A4 = 11906 twip
        Assert.InRange((int)size.Height!.Value, 16820, 16850);
        Assert.Equal("Hizmet Raporu", w.PackageProperties.Title);
    }

    [Fact]
    public async Task MultiPageDocxKeepsPageBreaksAndSizes()
    {
        using var doc = await PdfDocument.CreateAsync(new SizeF(595, 842), 1);
        await doc.AddTextAsync(0, "Birinci sayfa", 72, 760, 14, Fx.Font);
        await doc.InsertBlankPagesAsync(1, 1, new SizeF(842, 595));
        await doc.AddTextAsync(1, "İkinci sayfa yatay", 72, 500, 14, Fx.Font);
        var path = Tmp("iki.docx");
        await PdfConverter.ToDocxAsync(doc, path, new[] { 0, 1 });
        using var w = WordprocessingDocument.Open(path, false);
        var sects = w.MainDocumentPart!.Document.Body!.Descendants<W.SectionProperties>().ToList();
        Assert.Equal(2, sects.Count);
        Assert.Equal(W.PageOrientationValues.Landscape, sects[1].GetFirstChild<W.PageSize>()!.Orient!.Value);
        var text = w.MainDocumentPart.Document.Body!.InnerText;
        Assert.True(text.IndexOf("Birinci sayfa") < text.IndexOf("İkinci sayfa yatay"));
    }

    [Fact]
    public async Task WritesXlsxWithNumbers()
    {
        using var doc = await PdfDocument.OpenAsync(Fx.F("convert_sample.pdf"));
        var path = Tmp("rapor.xlsx");
        await PdfConverter.ToXlsxAsync(doc, path, new[] { 0 });
        using var x = SpreadsheetDocument.Open(path, false);
        var ws = x.WorkbookPart!.WorksheetParts.Single().Worksheet;
        var cells = ws.Descendants<S.Cell>().ToList();
        string Val(S.Cell c) => c.DataType?.Value == S.CellValues.InlineString ? c.InnerText : c.CellValue?.Text ?? "";
        var kalem = cells.Single(c => Val(c) == "Kalem");
        var row = cells.Where(c => c.Parent == kalem.Parent).ToList();
        Assert.Equal(new[] { "Kalem", "1250", "3437.5" }, row.Select(Val).ToArray());
        Assert.Equal(S.CellValues.Number, row[1].DataType!.Value);
        Assert.Contains(cells, c => Val(c) == "12800");
        Assert.Contains(cells, c => Val(c) == "Aylık Satışlar");
        Assert.Equal("Sayfa 1", x.WorkbookPart.Workbook.Descendants<S.Sheet>().Single().Name!.Value);
        Assert.Single(ws.Descendants<S.MergeCell>()); // “Aylık Satışlar” 3 sütunu kaplar
    }

    [Theory]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("-12,5", -12.5)]
    [InlineData("1250", 1250)]
    [InlineData("3.14", 3.14)]
    [InlineData("%15", 0.15)]
    [InlineData("12,50 TL", 12.5)]
    [InlineData("₺1.000", 1000)]
    public void ParsesTurkishNumbers(string s, double v)
    {
        Assert.True(XlsxWriter.TryNumber(s, out var d));
        Assert.Equal(v, d, 6);
    }

    [Theory]
    [InlineData("11.11.2026")]
    [InlineData("Kalem")]
    [InlineData("05xx 123 45 67")]
    [InlineData("1.2.3")]
    public void KeepsNonNumbersAsText(string s) => Assert.False(XlsxWriter.TryNumber(s, out _));

    [Fact]
    public async Task ScannedPageWithTableConvertsAfterOcr()
    {
        Assert.Null(OcrEngine.Unavailable());
        // Fikstürü çiz → görüntü PDF (taranmış gibi) → OCR → Word
        using var src = await PdfDocument.OpenAsync(Fx.F("convert_sample.pdf"));
        var img = await PdfTools.RenderAtDpiAsync(src, 0, 300);
        using var doc = await PdfDocument.CreateFromImagesAsync(new ImagePageSource[] { new BitmapImageSource(img, 300, 300) }, new ImagePageLayout());
        var ocr = await OcrService.RecognizeAsync(await PdfTools.RenderAtDpiAsync(doc, 0, 300), 300, "tur+eng");
        await doc.AddOcrLayerAsync(0, ocr, Fx.Font);
        var page = await LayoutAnalyzer.AnalyzeAsync(doc, 0);
        Assert.True(page.FromOcr);
        Assert.DoesNotContain(page.Blocks, b => b is ImageBlock { Box.Width: > 400 }); // taranmış sayfanın kendisi görsel olarak aktarılmaz
        // Taranmış sayfadaki fotoğraf (160×110 pt) metin dışı bölge olarak kesilir.
        var pic = Assert.Single(page.Blocks.OfType<ImageBlock>());
        Assert.InRange(pic.Box.Width, 135, 185); // fotoğrafın neredeyse beyaz kenar payı resim sayılmaz
        Assert.InRange(pic.Box.Height, 90, 135);
        var table = Assert.Single(page.Blocks.OfType<TableBlock>());
        Assert.Equal(4, table.Rows);
        Assert.Contains("Kalem", table.Cells.Single(c => c.Row == 2 && c.Col == 0).Text);
        var paras = page.Blocks.OfType<ParagraphBlock>().ToList();
        Assert.Contains(paras, p => p.Text.Contains("Hizmet Raporu 2026"));
        Assert.Contains(paras, p => p.Text.Contains("dönüştürme özelliğinin"));
        var path = Tmp("tarama.docx");
        DocxWriter.Write(path, new[] { page });
        using var w = WordprocessingDocument.Open(path, false);
        Assert.Contains("Kalem", w.MainDocumentPart!.Document.Body!.InnerText);
    }
}
