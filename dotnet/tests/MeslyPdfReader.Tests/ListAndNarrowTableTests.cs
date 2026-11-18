using DocumentFormat.OpenXml.Packaging;
using MeslyPdfReader.Convert;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MeslyPdfReader.Tests;

/// <summary>Dönüştürme kalitesi: liste algılama, gerçek Word numaralandırması, dar oluklu çizgisiz tablo.</summary>
public class ListAndNarrowTableTests
{
    [Theory]
    [InlineData("•", true)] [InlineData("-", true)] [InlineData("1.", true)] [InlineData("12)", true)]
    [InlineData("a)", true)] [InlineData("(3)", true)] [InlineData("Merhaba", false)] [InlineData("2025", false)] [InlineData("v1.", false)]
    public void RecognizesListMarkers(string w, bool expected)
    {
        Assert.Equal(expected, LayoutAnalyzer.IsListMarker(w, true));
    }

    [Fact]
    public async Task DetectsBulletedAndNumberedListsFromPdf()
    {
        using var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1);
        await doc.AddTextAsync(0, "Hedefler aşağıdaki gibidir ve yıl sonunda gözden geçirilir.", 72, 760, 12, Fx.Font);
        string[] items = { "Ciroyu artırmak", "Maliyeti azaltmak", "Yeni bölge açmak" };
        for (int i = 0; i < items.Length; i++)
        {
            await doc.AddTextAsync(0, "•", 90, 730 - i * 18, 12, Fx.Font);
            await doc.AddTextAsync(0, items[i], 106, 730 - i * 18, 12, Fx.Font);
        }
        for (int i = 0; i < items.Length; i++)
        {
            await doc.AddTextAsync(0, $"{i + 1}.", 90, 650 - i * 18, 12, Fx.Font);
            await doc.AddTextAsync(0, items[i], 108, 650 - i * 18, 12, Fx.Font);
        }
        var page = await LayoutAnalyzer.AnalyzeAsync(doc, 0);
        var paras = page.Blocks.OfType<ParagraphBlock>().ToList();
        var bullets = paras.Where(p => p.IsListItem && !p.IsNumbered).ToList();
        var numbered = paras.Where(p => p.IsNumbered).ToList();
        Assert.Equal(3, bullets.Count);
        Assert.Equal(3, numbered.Count);
        Assert.Equal("Ciroyu artırmak", bullets[0].Text);          // işaret metne karışmaz
        Assert.Equal("Yeni bölge açmak", numbered[2].Text);

        var path = Path.Combine(Fx.TempDir(), "list.docx");
        DocxWriter.Write(path, new[] { page });
        using var wp = WordprocessingDocument.Open(path, false);
        var body = wp.MainDocumentPart!.Document.Body!;
        Assert.Equal(6, body.Descendants<W.NumberingProperties>().Count());   // her öğe gerçek Word liste öğesi
        var numIds = body.Descendants<W.NumberingId>().Select(n => n.Val!.Value).Distinct().ToList();
        Assert.Equal(2, numIds.Count);                                         // madde işareti + numaralı liste
        Assert.NotNull(wp.MainDocumentPart.NumberingDefinitionsPart);
    }

    [Fact]
    public async Task FindsBorderlessTableWithNarrowGutters()
    {
        // Rapor tipi: sütunlar arası yalnız ~6 pt; satır aralığı 30 pt; ilk sütunda uzun etiket.
        using var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1);
        string[][] rows =
        {
            new[] { "Kalem", "2024", "2025", "Değişim" },
            new[] { "Net satışlar", "12.450.000", "15.980.000", "%28,4" },
            new[] { "Brüt kâr", "4.120.000", "5.610.000", "%36,2" },
            new[] { "Faaliyet gideri", "2.870.000", "3.240.000", "%12,9" },
            new[] { "Net kâr", "1.250.000", "2.370.000", "%89,6" },
        };
        double[] x = { 72, 160, 232, 304 };
        await doc.AddTextAsync(0, "Finansal özet aşağıdaki tabloda verilmiştir ve denetlenmiştir.", 72, 760, 11, Fx.Font);
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < 4; c++)
                await doc.AddTextAsync(0, rows[r][c], x[c], 700 - r * 30, 11, Fx.Font);
        var page = await LayoutAnalyzer.AnalyzeAsync(doc, 0);
        var t = Assert.Single(page.Blocks.OfType<TableBlock>());
        Assert.Equal(5, t.Rows);
        Assert.True(t.Cols == 4, "kenarlar: " + string.Join(",", t.ColEdges.Select(e => e.ToString("F1"))));
        Assert.Contains(t.Cells, c => c.Content.Any(p => p.Text.Trim() == "15.980.000"));
    }

    [Fact]
    public async Task WritesRealWordColumnsForTwoColumnPage()
    {
        using var doc = await PdfDocument.CreateAsync(PaperSize.A4.ToPoints(), 1);
        await doc.AddTextAsync(0, "Başlık", 250, 780, 18, Fx.Font);
        string[] left = { "Sol sütun birinci satır metni", "Sol sütun ikinci satır metni", "Sol sütun üçüncü satır metni", "Sol sütun dördüncü satır metni" };
        string[] right = { "Sağ sütun birinci satır metni", "Sağ sütun ikinci satır metni", "Sağ sütun üçüncü satır metni", "Sağ sütun dördüncü satır metni" };
        for (int i = 0; i < 4; i++)
        {
            await doc.AddTextAsync(0, left[i], 72, 700 - i * 14, 11, Fx.Font);
            await doc.AddTextAsync(0, right[i], 320, 700 - i * 14, 11, Fx.Font);
        }
        var page = await LayoutAnalyzer.AnalyzeAsync(doc, 0);
        Assert.Contains(page.Blocks, b => b is ColumnsBlock { Kind: ColumnsKind.Start, Widths.Count: 2 });
        Assert.Contains(page.Blocks, b => b is ColumnsBlock { Kind: ColumnsKind.Break });

        var path = Path.Combine(Fx.TempDir(), "cols.docx");
        DocxWriter.Write(path, new[] { page });
        using var wp = WordprocessingDocument.Open(path, false);
        var cols = wp.MainDocumentPart!.Document.Body!.Descendants<W.Columns>().ToList();
        Assert.Single(cols);
        Assert.Equal(2, cols[0].Elements<W.Column>().Count());
        Assert.Contains(wp.MainDocumentPart.Document.Body!.Descendants<W.Break>(), br => br.Type?.Value == W.BreakValues.Column);
        // Sütun sırası: önce tüm sol metin, sonra sağ.
        string all = string.Concat(wp.MainDocumentPart.Document.Body!.Descendants<W.Text>().Select(t => t.Text + " "));
        Assert.True(all.IndexOf("Sol sütun dördüncü", StringComparison.Ordinal) < all.IndexOf("Sağ sütun birinci", StringComparison.Ordinal));
    }
}
