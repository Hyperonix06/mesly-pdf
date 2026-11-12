using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MeslyPdfReader.Core;
using W = DocumentFormat.OpenXml.Wordprocessing;
using WpfBlock = System.Windows.Documents.Block;
using WpfParagraph = System.Windows.Documents.Paragraph;
using WpfRun = System.Windows.Documents.Run;
using WpfTable = System.Windows.Documents.Table;
using TableCell = System.Windows.Documents.TableCell;

namespace MeslyPdfReader.App.Services;

/// <summary>
/// OCR sonucu düzenleyicisindeki (RichTextBox) belgeyi dışa aktarır: Word (.docx; yazı tipi, boyut, kalın/eğik/altı
/// çizili, renk, hizalama, madde işaretleri, tablolar, sayfa sonları), düz metin ve düzen modelinden FlowDocument kurar.
/// </summary>
public static class FlowDocumentExport
{
    // ─────────────── Düzen modeli → FlowDocument ───────────────

    /// <summary>OCR'dan gelen sayfa düzenlerini düzenlenebilir belgeye çevirir. Her sayfa yeni sayfadan başlar.</summary>
    public static FlowDocument Build(IReadOnlyList<PageLayout> pages, bool keepLayout)
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 14.7, PagePadding = new Thickness(56), ColumnWidth = double.PositiveInfinity, Background = Brushes.White, Foreground = Brushes.Black };
        for (int p = 0; p < pages.Count; p++)
        {
            bool first = true;
            var blocks = pages[p].Blocks;
            if (blocks.Count == 0)
            {
                var empty = new WpfParagraph(new WpfRun($"(Sayfa {pages[p].PageIndex + 1}: metin bulunamadı)") { Foreground = Brushes.Gray, FontStyle = FontStyles.Italic }) { Tag = pages[p].PageIndex };
                if (p > 0) empty.BreakPageBefore = true;
                doc.Blocks.Add(empty);
                continue;
            }
            foreach (var b in blocks)
            {
                WpfBlock? block = b switch
                {
                    ParagraphBlock pb => keepLayout ? Para(pb) : new WpfParagraph(new WpfRun(pb.Text)) { Margin = new Thickness(0, 0, 0, 8) },
                    TableBlock tb when keepLayout => Table(tb),
                    TableBlock tb => new WpfParagraph(new WpfRun(string.Join("\n", Enumerable.Range(0, tb.Rows).Select(r => string.Join("\t", tb.Cells.Where(c => c.Row == r).OrderBy(c => c.Col).Select(c => c.Text))))) ),
                    _ => null,
                };
                if (block is null) continue;
                if (first) { block.Tag = pages[p].PageIndex; if (p > 0) block.BreakPageBefore = true; first = false; }
                doc.Blocks.Add(block);
            }
        }
        return doc;
    }

    private static WpfParagraph Para(ParagraphBlock pb)
    {
        var para = new WpfParagraph
        {
            TextAlignment = pb.Align switch { ParaAlign.Center => TextAlignment.Center, ParaAlign.Right => TextAlignment.Right, ParaAlign.Justify => TextAlignment.Justify, _ => TextAlignment.Left },
            Margin = new Thickness(Math.Min(pb.Indent, 120) * 96 / 72, Math.Min(pb.SpaceBefore, 24) * 96 / 72 * 0.6, 0, 4),
        };
        foreach (var r in pb.Runs)
        {
            var run = new WpfRun(r.Text)
            {
                FontSize = Math.Clamp(r.Size, 6, 72) * 96 / 72,
                FontWeight = r.Bold || pb.HeadingLevel > 0 ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = r.Italic ? FontStyles.Italic : FontStyles.Normal,
            };
            if (r.FontFamily is { Length: > 0 } f) run.FontFamily = new FontFamily(f + ", Segoe UI");
            if ((r.Rgb & 0xFFFFFF) != 0) run.Foreground = new SolidColorBrush(Color.FromRgb((byte)(r.Rgb >> 16), (byte)(r.Rgb >> 8), (byte)r.Rgb));
            para.Inlines.Add(run);
        }
        return para;
    }

    private static WpfTable Table(TableBlock tb)
    {
        var t = new WpfTable { CellSpacing = 0, BorderBrush = Brushes.Black, BorderThickness = new Thickness(0.5) };
        double total = Math.Max(1, tb.ColEdges[^1] - tb.ColEdges[0]);
        for (int c = 0; c < tb.Cols; c++) t.Columns.Add(new TableColumn { Width = new GridLength((tb.ColEdges[c + 1] - tb.ColEdges[c]) / total, GridUnitType.Star) });
        var group = new TableRowGroup();
        for (int r = 0; r < tb.Rows; r++)
        {
            var row = new TableRow();
            foreach (var cell in tb.Cells.Where(c => c.Row == r).OrderBy(c => c.Col))
            {
                var tc = new TableCell { ColumnSpan = Math.Max(1, cell.ColSpan), BorderBrush = Brushes.Black, BorderThickness = new Thickness(0.5), Padding = new Thickness(5, 3, 5, 3) };
                if (cell.Content.Count == 0) tc.Blocks.Add(new WpfParagraph());
                foreach (var p in cell.Content) { var para = Para(p); para.Margin = new Thickness(0); tc.Blocks.Add(para); }
                row.Cells.Add(tc);
            }
            group.Rows.Add(row);
        }
        t.RowGroups.Add(group);
        return t;
    }

    // ─────────────── Düz metin ───────────────

    public static string PlainText(FlowDocument doc) => new TextRange(doc.ContentStart, doc.ContentEnd).Text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    public static void SaveText(FlowDocument doc, string path) => File.WriteAllText(path, PlainText(doc), new System.Text.UTF8Encoding(true));

    // ─────────────── Word (.docx) ───────────────

    public static void SaveDocx(FlowDocument doc, string path, string? title = null)
    {
        var tmp = path + ".mesly-tmp.docx";
        try
        {
            using (var w = WordprocessingDocument.Create(tmp, WordprocessingDocumentType.Document))
            {
                var main = w.AddMainDocumentPart();
                main.Document = new W.Document(new W.Body());
                AddNumbering(main);
                var body = main.Document.Body!;
                foreach (var b in doc.Blocks) foreach (var e in Blocks(b, main)) body.Append(e);
                body.Append(new W.SectionProperties(new W.PageSize { Width = 11906, Height = 16838 },
                    new W.PageMargin { Left = 1134, Right = 1134, Top = 1134, Bottom = 1134, Header = 708, Footer = 708, Gutter = 0 }));
                if (title is not null) { w.PackageProperties.Title = title; w.PackageProperties.Creator = "Mesly PDF Reader"; }
                main.Document.Save();
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static void AddNumbering(MainDocumentPart main)
    {
        var part = main.AddNewPart<NumberingDefinitionsPart>();
        part.Numbering = new W.Numbering(
            new W.AbstractNum(new W.Level(new W.NumberingFormat { Val = W.NumberFormatValues.Bullet }, new W.LevelText { Val = "•" },
                new W.PreviousParagraphProperties(new W.Indentation { Left = "720", Hanging = "360" })) { LevelIndex = 0 }) { AbstractNumberId = 1 },
            new W.AbstractNum(new W.Level(new W.StartNumberingValue { Val = 1 }, new W.NumberingFormat { Val = W.NumberFormatValues.Decimal }, new W.LevelText { Val = "%1." },
                new W.PreviousParagraphProperties(new W.Indentation { Left = "720", Hanging = "360" })) { LevelIndex = 0 }) { AbstractNumberId = 2 },
            new W.NumberingInstance(new W.AbstractNumId { Val = 1 }) { NumberID = 1 },
            new W.NumberingInstance(new W.AbstractNumId { Val = 2 }) { NumberID = 2 });
    }

    private static IEnumerable<OpenXmlElement> Blocks(WpfBlock b, MainDocumentPart main, int numId = 0)
    {
        switch (b)
        {
            case WpfParagraph p: yield return Paragraph(p, numId); break;
            case List l:
                int id = l.MarkerStyle is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman ? 2 : 1;
                foreach (var item in l.ListItems) foreach (var ib in item.Blocks) foreach (var e in Blocks(ib, main, id)) yield return e;
                break;
            case Section s: foreach (var sb in s.Blocks) foreach (var e in Blocks(sb, main, numId)) yield return e; break;
            case WpfTable t: yield return Table(t, main); yield return new W.Paragraph(); break;
        }
    }

    private static W.Paragraph Paragraph(WpfParagraph p, int numId)
    {
        var ppr = new W.ParagraphProperties();
        if (p.BreakPageBefore) ppr.Append(new W.PageBreakBefore());
        if (numId > 0) ppr.Append(new W.NumberingProperties(new W.NumberingLevelReference { Val = 0 }, new W.NumberingId { Val = numId }));
        ppr.Append(new W.SpacingBetweenLines { Before = Twips(p.Margin.Top), After = Twips(p.Margin.Bottom) });
        if (p.Margin.Left > 1 && numId == 0) ppr.Append(new W.Indentation { Left = Twips(p.Margin.Left) });
        ppr.Append(new W.Justification
        {
            Val = p.TextAlignment switch { TextAlignment.Center => W.JustificationValues.Center, TextAlignment.Right => W.JustificationValues.Right, TextAlignment.Justify => W.JustificationValues.Both, _ => W.JustificationValues.Left },
        });
        var para = new W.Paragraph(ppr);
        foreach (var inline in p.Inlines) foreach (var r in Inlines(inline)) para.Append(r);
        return para;
    }

    private static string Twips(double dip) => ((int)Math.Round(Math.Max(0, dip) * 72 / 96 * 20)).ToString();

    private static IEnumerable<OpenXmlElement> Inlines(Inline inline)
    {
        switch (inline)
        {
            case WpfRun r when r.Text.Length > 0:
                yield return Run(r, r.Text);
                break;
            case LineBreak: yield return new W.Run(new W.Break()); break;
            case Span s:
                foreach (var child in s.Inlines) foreach (var e in Inlines(child)) yield return e;
                break;
        }
    }

    private static W.Run Run(TextElement r, string text)
    {
        var rpr = new W.RunProperties();
        var family = r.FontFamily.Source.Split(',')[0].Trim();
        rpr.Append(new W.RunFonts { Ascii = family, HighAnsi = family, ComplexScript = family, EastAsia = family });
        if (r.FontWeight.ToOpenTypeWeight() >= 600) rpr.Append(new W.Bold());
        if (r.FontStyle == FontStyles.Italic) rpr.Append(new W.Italic());
        if (r is Inline il && (il.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true || (il.Parent is Underline)))
            rpr.Append(new W.Underline { Val = W.UnderlineValues.Single });
        if (r is Inline il2 && il2.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true) rpr.Append(new W.Strike());
        if (r.Foreground is SolidColorBrush sc && (sc.Color.R | sc.Color.G | sc.Color.B) != 0) rpr.Append(new W.Color { Val = $"{sc.Color.R:X2}{sc.Color.G:X2}{sc.Color.B:X2}" });
        var hp = ((int)Math.Round(Math.Clamp(r.FontSize * 72 / 96, 4, 200) * 2)).ToString();
        rpr.Append(new W.FontSize { Val = hp }, new W.FontSizeComplexScript { Val = hp });
        return new W.Run(rpr, new W.Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static W.Table Table(WpfTable t, MainDocumentPart main)
    {
        const int width = 9638; // A4 metin genişliği (twip)
        double stars = t.Columns.Sum(c => c.Width.IsStar ? c.Width.Value : 1);
        var widths = t.Columns.Select(c => (int)(width * (c.Width.IsStar ? c.Width.Value : 1) / Math.Max(0.001, stars))).ToArray();
        if (widths.Length == 0) widths = new[] { width };
        var table = new W.Table(new W.TableProperties(
            new W.TableWidth { Type = W.TableWidthUnitValues.Dxa, Width = widths.Sum().ToString() },
            new W.TableBorders(new W.TopBorder { Val = W.BorderValues.Single, Size = 4 }, new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.LeftBorder { Val = W.BorderValues.Single, Size = 4 }, new W.RightBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 }, new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 }),
            new W.TableLayout { Type = W.TableLayoutValues.Fixed }));
        var grid = new W.TableGrid();
        foreach (var w in widths) grid.Append(new W.GridColumn { Width = w.ToString() });
        table.Append(grid);
        foreach (var group in t.RowGroups)
            foreach (var row in group.Rows)
            {
                var tr = new W.TableRow();
                int col = 0;
                foreach (var cell in row.Cells)
                {
                    int span = Math.Max(1, cell.ColumnSpan);
                    int w = widths.Skip(col).Take(span).Sum();
                    var tcpr = new W.TableCellProperties(new W.TableCellWidth { Type = W.TableWidthUnitValues.Dxa, Width = w.ToString() });
                    if (span > 1) tcpr.Append(new W.GridSpan { Val = span });
                    var tc = new W.TableCell(tcpr);
                    bool any = false;
                    foreach (var b in cell.Blocks) foreach (var e in Blocks(b, main)) { tc.Append(e); any = true; }
                    if (!any) tc.Append(new W.Paragraph());
                    tr.Append(tc);
                    col += span;
                }
                table.Append(tr);
            }
        return table;
    }

    // ─────────────── PDF (Windows'un “Microsoft Print to PDF” yazıcısı) ───────────────

    public const string PdfPrinter = "Microsoft Print to PDF";

    public static bool PdfPrinterAvailable()
    {
        try
        {
            using var server = new LocalPrintServer();
            return server.GetPrintQueues().Any(q => q.Name.Equals(PdfPrinter, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    /// <summary>
    /// Belgeyi Windows'un yerleşik PDF yazıcısıyla PDF'ye yazdırır (metin seçilebilir kalır). Windows dosya adını kendi
    /// penceresinde sorar. Yazıcı yoksa false.
    /// </summary>
    public static bool PrintToPdf(FlowDocument source, string jobName)
    {
        using var server = new LocalPrintServer();
        var queue = server.GetPrintQueues().FirstOrDefault(q => q.Name.Equals(PdfPrinter, StringComparison.OrdinalIgnoreCase));
        if (queue is null) return false;
        // Kopya üzerinde sayfalama (düzenleyicideki belge etkilenmesin).
        var xaml = System.Windows.Markup.XamlWriter.Save(source);
        var copy = (FlowDocument)System.Windows.Markup.XamlReader.Parse(xaml);
        const double a4w = 793.7, a4h = 1122.5;
        copy.PageWidth = a4w; copy.PageHeight = a4h; copy.PagePadding = new Thickness(75); copy.ColumnWidth = double.PositiveInfinity;
        var writer = PrintQueue.CreateXpsDocumentWriter(queue);
        var ticket = queue.DefaultPrintTicket.Clone();
        ticket.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4);
        writer.Write(((IDocumentPaginatorSource)copy).DocumentPaginator, ticket);
        return true;
    }
}
