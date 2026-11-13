using DocumentFormat.OpenXml.Packaging;
using MeslyPdfReader.Convert;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace MeslyPdfReader.Tests;

/// <summary>Çizgisiz (hizalamayla oluşan) tabloların sözcük konumlarından bulunması ve Word/Excel'e aktarılması.</summary>
public class BorderlessTableTests
{
    private static readonly string[][] Data =
    {
        new[] { "Ürün", "Adet", "Birim Fiyat", "Tutar" },
        new[] { "Çay bardağı", "12", "35,50", "426,00" },
        new[] { "Şeker kabı", "3", "120,00", "360,00" },
        new[] { "Ağaç kaşık", "40", "8,25", "330,00" },
        new[] { "Toplam", "55", "", "1.116,00" },
    };

    private static async Task<PdfDocument> TableDocAsync(bool withIntro = true)
    {
        var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1);
        double[] x = { 72, 230, 320, 430 };
        if (withIntro) await doc.AddTextAsync(0, "Aşağıdaki tablo satış özetini gösterir ve ay sonunda güncellenir.", 72, 760, 12, Fx.Font);
        for (int r = 0; r < Data.Length; r++)
            for (int c = 0; c < 4; c++)
                if (Data[r][c].Length > 0) await doc.AddTextAsync(0, Data[r][c], x[c], 700 - r * 20, 12, Fx.Font);
        await doc.AddTextAsync(0, "Fiyatlara KDV dahildir. Bu belge bilgi amaçlıdır.", 72, 560, 12, Fx.Font);
        return doc;
    }

    [Fact]
    public async Task FindsAlignedTableWithoutRuling()
    {
        using var doc = await TableDocAsync();
        var page = await LayoutAnalyzer.AnalyzeAsync(doc, 0);
        var t = Assert.Single(page.Blocks.OfType<TableBlock>());
        Assert.True(t.Borderless);
        Assert.Equal(5, t.Rows);
        Assert.Equal(4, t.Cols);
        Assert.Equal("Birim Fiyat", t.Cells.Single(c => c.Row == 0 && c.Col == 2).Text);
        Assert.Equal("Çay bardağı", t.Cells.Single(c => c.Row == 1 && c.Col == 0).Text);
        Assert.Equal("1.116,00", t.Cells.Single(c => c.Row == 4 && c.Col == 3).Text);
        // Tablo dışındaki cümleler paragraf olarak kalır, tabloya karışmaz.
        var paras = page.Blocks.OfType<ParagraphBlock>().Select(p => p.Text).ToList();
        Assert.Contains(paras, p => p.StartsWith("Aşağıdaki tablo"));
        Assert.Contains(paras, p => p.StartsWith("Fiyatlara KDV"));
        Assert.DoesNotContain(paras, p => p.Contains("Çay bardağı"));
        // Okuma sırası: giriş → tablo → dipnot
        var order = page.Blocks.Select(b => b is TableBlock ? "T" : ((ParagraphBlock)b).Text[..4]).ToList();
        Assert.True(order.IndexOf("Aşağ") < order.IndexOf("T") && order.IndexOf("T") < order.IndexOf("Fiya"), string.Join(",", order));
    }

    [Fact]
    public async Task WordTableHasNoBordersAndExcelGetsNumbers()
    {
        using var doc = await TableDocAsync();
        var dx = Path.Combine(Fx.TempDir(), "cizgisiz.docx");
        await PdfConverter.ToDocxAsync(doc, dx, new[] { 0 });
        using (var w = WordprocessingDocument.Open(dx, false))
        {
            var tbl = Assert.Single(w.MainDocumentPart!.Document.Body!.Descendants<W.Table>());
            Assert.Null(tbl.GetFirstChild<W.TableProperties>()!.TableStyle); // "TableGrid" yok
            var b = tbl.GetFirstChild<W.TableProperties>()!.GetFirstChild<W.TableBorders>();
            Assert.NotNull(b);
            Assert.Equal(W.BorderValues.None, b!.TopBorder!.Val!.Value);
            Assert.Equal(5, tbl.Elements<W.TableRow>().Count());
        }
        var xx = Path.Combine(Fx.TempDir(), "cizgisiz.xlsx");
        await PdfConverter.ToXlsxAsync(doc, xx, new[] { 0 });
        using var x = SpreadsheetDocument.Open(xx, false);
        var cells = x.WorkbookPart!.WorksheetParts.First().Worksheet.Descendants<S.Cell>().ToList();
        Assert.Contains(cells, c => c.DataType?.Value != S.CellValues.SharedString && c.CellValue?.Text == "426");
    }

    [Fact]
    public async Task ProseAndTwoColumnTextAreNotTables()
    {
        using var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1);
        string[] left = { "Sol sütun birinci satır metni burada ve biraz uzun", "Sol sütun ikinci satır metni burada ve biraz uzun", "Sol sütun üçüncü satır metni burada ve biraz uzun", "Sol sütun dördüncü satır metni burada ve biraz uzun" };
        string[] right = { "Sağ sütun birinci satır metni burada ve biraz uzun", "Sağ sütun ikinci satır metni burada ve biraz uzun", "Sağ sütun üçüncü satır metni burada ve biraz uzun", "Sağ sütun dördüncü satır metni burada ve biraz uzun" };
        for (int i = 0; i < 4; i++)
        {
            await doc.AddTextAsync(0, left[i], 60, 700 - i * 18, 11, Fx.Font);
            await doc.AddTextAsync(0, right[i], 320, 700 - i * 18, 11, Fx.Font);
        }
        var page = await LayoutAnalyzer.AnalyzeAsync(doc, 0);
        Assert.Empty(page.Blocks.OfType<TableBlock>());

        using var prose = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1);
        for (int i = 0; i < 5; i++) await prose.AddTextAsync(0, "Bu bir düz paragraf satırıdır ve sütun içermez, bütün satır boyunca akar.", 72, 700 - i * 16, 12, Fx.Font);
        Assert.Empty((await LayoutAnalyzer.AnalyzeAsync(prose, 0)).Blocks.OfType<TableBlock>());
    }

    [Fact]
    public async Task TwoRowsAreNotEnoughForATable()
    {
        using var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1);
        await doc.AddTextAsync(0, "Ad", 72, 700, 12, Fx.Font); await doc.AddTextAsync(0, "Soyad", 300, 700, 12, Fx.Font);
        await doc.AddTextAsync(0, "Ali", 72, 680, 12, Fx.Font); await doc.AddTextAsync(0, "Yılmaz", 300, 680, 12, Fx.Font);
        Assert.Empty((await LayoutAnalyzer.AnalyzeAsync(doc, 0)).Blocks.OfType<TableBlock>());
    }

    [Fact]
    public async Task ScannedAlignedTableSurvivesOcrAndBecomesTable()
    {
        if (MeslyPdfReader.Ocr.OcrService.Unavailable("tur+eng", MeslyPdfReader.Ocr.OcrBackend.Auto) is { } why) Assert.Fail(why);
        using var src = await TableDocAsync();
        var img = await PdfTools.RenderAtDpiAsync(src, 0, 300);
        using var scan = await PdfDocument.CreateFromImagesAsync(new ImagePageSource[] { new BitmapImageSource(img, 300, 300) }, new ImagePageLayout());
        var ocr = await MeslyPdfReader.Ocr.OcrService.RecognizeAsync(img, 300, "tur+eng");
        await scan.AddOcrLayerAsync(0, ocr, Fx.Font);
        var page = await LayoutAnalyzer.AnalyzeAsync(scan, 0);
        Assert.True(page.FromOcr);
        var t = Assert.Single(page.Blocks.OfType<TableBlock>());
        Assert.Equal(5, t.Rows);
        Assert.Equal(4, t.Cols);
        Assert.Equal("1.116,00", t.Cells.Single(c => c.Row == 4 && c.Col == 3).Text);
        Assert.Equal("Çay bardağı", t.Cells.Single(c => c.Row == 1 && c.Col == 0).Text);
    }
}
