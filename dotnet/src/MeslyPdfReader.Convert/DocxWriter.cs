using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace MeslyPdfReader.Convert;

/// <summary>
/// Düzen modelinden düzenlenebilir Word belgesi yazar: metin akar (sayfa sonları ve sayfa boyutları korunur),
/// biçim (yazı tipi, boyut, kalın, eğik, renk, hizalama, satır aralığı), başlıklar, tablolar ve görseller.
/// </summary>
public static class DocxWriter
{
    /// <summary>Görseli kodlar: (veri, "image/png" | "image/jpeg"). Varsayılan PNG.</summary>
    public static Func<BgraImage, bool, (byte[] Data, string ContentType)> ImageEncoder { get; set; } = (img, _) =>
    {
        using var ms = new MemoryStream();
        PngEncoder.Write(img, ms);
        return (ms.ToArray(), "image/png");
    };

    private const int Twip = 20;            // 1 pt = 20 twip
    private const long Emu = 12700;         // 1 pt = 12700 EMU

    public static void Write(string path, IReadOnlyList<PageLayout> pages, string? title = null)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        AddStyles(main);
        var body = main.Document.Body!;
        uint imageId = 1;
        for (int p = 0; p < pages.Count; p++)
        {
            var page = pages[p];
            var area = page.TextArea;
            double ml = Math.Clamp(area.X0, 18, 108), mr = Math.Clamp(page.Width - area.X1, 18, 108);
            double mt = Math.Clamp(area.Y0, 18, 108), mb = Math.Clamp(page.Height - area.Y1, 18, 108);
            double contentW = page.Width - ml - mr;
            int written = 0;
            double? prevBottom = null;
            foreach (var b in page.Blocks)
            {
                // Tablo ve resimlerin üstündeki boşluk (paragraflar kendi SpaceBefore değerini taşır).
                double gap = prevBottom is { } pbm ? Math.Clamp(b.Box.Y0 - pbm, 0, 72) : 0;
                switch (b)
                {
                    case ParagraphBlock pb: body.Append(Para(pb, area)); written++; break;
                    case TableBlock tb:
                        if (gap > 6) body.Append(Spacer(gap - 2));
                        body.Append(Table(tb, contentW));
                        body.Append(Spacer(1)); // Word iki tabloyu birleştirmesin diye zorunlu boş paragraf, 1 pt
                        written++; break;
                    case ImageBlock ib: body.Append(Image(main, ib, contentW, ref imageId, gap)); written++; break;
                }
                prevBottom = b.Box.Y1;
            }
            if (written == 0) body.Append(new Paragraph());
            var sect = new SectionProperties(
                new PageSize { Width = (UInt32Value)(uint)Math.Round(page.Width * Twip), Height = (UInt32Value)(uint)Math.Round(page.Height * Twip),
                    Orient = page.Width > page.Height ? PageOrientationValues.Landscape : PageOrientationValues.Portrait },
                new PageMargin { Left = (UInt32Value)(uint)(ml * Twip), Right = (UInt32Value)(uint)(mr * Twip), Top = (int)(mt * Twip), Bottom = (int)(mb * Twip), Header = 360, Footer = 360, Gutter = 0 },
                new SectionType { Val = SectionMarkValues.NextPage });
            if (p < pages.Count - 1)
                body.Append(new Paragraph(new ParagraphProperties(sect, new SpacingBetweenLines { After = "0", Line = "20", LineRule = LineSpacingRuleValues.Exact })));
            else body.Append(sect);
        }
        if (title is not null)
        {
            doc.PackageProperties.Title = title;
            doc.PackageProperties.Creator = "Mesly PDF Reader";
        }
        main.Document.Save();
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var sp = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" },
                    new FontSize { Val = "22" }, new Languages { Val = "tr-TR" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(new StyleName { Val = "Normal" }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            Heading(1), Heading(2),
            new Style(new StyleName { Val = "Table Grid" }, new BasedOn { Val = "TableNormal" },
                new StyleTableProperties(new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4 }, new BottomBorder { Val = BorderValues.Single, Size = 4 },
                    new LeftBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })))
            { Type = StyleValues.Table, StyleId = "TableGrid" });
        sp.Styles = styles;

        static Style Heading(int level) => new(
            new StyleName { Val = $"heading {level}" }, new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" }, new PrimaryStyle(),
            new StyleParagraphProperties(new KeepNext(), new OutlineLevel { Val = level - 1 }),
            new StyleRunProperties(new Bold()))
        { Type = StyleValues.Paragraph, StyleId = $"Heading{level}" };
    }

    private static Paragraph Para(ParagraphBlock pb, Box area)
    {
        var ppr = new ParagraphProperties();
        if (pb.HeadingLevel > 0) ppr.Append(new ParagraphStyleId { Val = $"Heading{pb.HeadingLevel}" });
        ppr.Append(new SpacingBetweenLines
        {
            Before = ((int)Math.Round(pb.SpaceBefore * Twip * 0.8)).ToString(),
            After = "0",
            // Satır aralığı: PDF'deki satır adımı / (yazı boyutu × 1,17) → Word “çoklu” satır aralığı (240 = tek).
            Line = ((int)Math.Round(Math.Clamp(pb.LineSpacing / 1.17, 0.85, 3) * 240)).ToString(),
            LineRule = LineSpacingRuleValues.Auto,
        });
        if (pb.Indent > 4 && pb.Align is ParaAlign.Left or ParaAlign.Justify) ppr.Append(new Indentation { Left = ((int)(pb.Indent * Twip)).ToString() });
        ppr.Append(new Justification
        {
            Val = pb.Align switch { ParaAlign.Center => JustificationValues.Center, ParaAlign.Right => JustificationValues.Right, ParaAlign.Justify => JustificationValues.Both, _ => JustificationValues.Left },
        });
        var para = new Paragraph(ppr);
        foreach (var r in pb.Runs) para.Append(Run(r));
        return para;
    }

    private static Run Run(TextRun r)
    {
        var rpr = new RunProperties();
        if (r.FontFamily is { } f) rpr.Append(new RunFonts { Ascii = f, HighAnsi = f, ComplexScript = f, EastAsia = f });
        if (r.Bold) rpr.Append(new Bold());
        if (r.Italic) rpr.Append(new Italic());
        if ((r.Rgb & 0xFFFFFF) != 0) rpr.Append(new Color { Val = $"{r.Rgb & 0xFFFFFF:X6}" });
        var hp = ((int)Math.Round(Math.Clamp(r.Size, 4, 200) * 2)).ToString();
        rpr.Append(new FontSize { Val = hp }, new FontSizeComplexScript { Val = hp });
        return new Run(rpr, new Text(r.Text) { Space = SpaceProcessingModeValues.Preserve });
    }

    private static Table Table(TableBlock tb, double contentWidth)
    {
        double scale = Math.Min(1, contentWidth / Math.Max(1, tb.Box.Width));
        var widths = Enumerable.Range(0, tb.Cols).Select(c => (int)Math.Round((tb.ColEdges[c + 1] - tb.ColEdges[c]) * scale * Twip)).ToArray();
        var table = new Table(new TableProperties(new TableStyle { Val = "TableGrid" },
            new TableWidth { Type = TableWidthUnitValues.Dxa, Width = widths.Sum().ToString() },
            new TableLayout { Type = TableLayoutValues.Fixed },
            new TableCellMarginDefault(new TopMargin { Width = "30", Type = TableWidthUnitValues.Dxa }, new BottomMargin { Width = "30", Type = TableWidthUnitValues.Dxa },
                new TableCellLeftMargin { Width = 80, Type = TableWidthValues.Dxa }, new TableCellRightMargin { Width = 80, Type = TableWidthValues.Dxa })));
        var grid = new TableGrid();
        foreach (var w in widths) grid.Append(new GridColumn { Width = w.ToString() });
        table.Append(grid);
        for (int r = 0; r < tb.Rows; r++)
        {
            int h = (int)Math.Round((tb.RowEdges[r + 1] - tb.RowEdges[r]) * Twip);
            var row = new TableRow(new TableRowProperties(new TableRowHeight { Val = (UInt32Value)(uint)Math.Max(200, h), HeightType = HeightRuleValues.AtLeast }));
            foreach (var cell in tb.Cells.Where(c => c.Row == r).OrderBy(c => c.Col))
            {
                int w = widths.Skip(cell.Col).Take(cell.ColSpan).Sum();
                var tcpr = new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = w.ToString() });
                if (cell.ColSpan > 1) tcpr.Append(new GridSpan { Val = cell.ColSpan });
                var tc = new DocumentFormat.OpenXml.Wordprocessing.TableCell(tcpr);
                if (cell.Content.Count == 0) tc.Append(new Paragraph());
                foreach (var p in cell.Content)
                {
                    var para = Para(p with { Indent = 0, SpaceBefore = 0, HeadingLevel = 0 }, p.Box);
                    tc.Append(para);
                }
                row.Append(tc);
            }
            table.Append(row);
        }
        return table;
    }

    /// <summary>Belirli yükseklikte (pt) boş paragraf: tablo üstü boşluk / tabloları ayırma.</summary>
    private static Paragraph Spacer(double heightPt) => new(new ParagraphProperties(
        new SpacingBetweenLines { Before = "0", After = "0", Line = ((int)Math.Max(20, Math.Round(heightPt * Twip))).ToString(), LineRule = LineSpacingRuleValues.Exact }),
        new ParagraphMarkRunProperties(new FontSize { Val = "2" }));

    private static Paragraph Image(MainDocumentPart main, ImageBlock ib, double contentWidth, ref uint id, double gapBefore)
    {
        var (data, type) = ImageEncoder(ib.Image, ib.PreferJpeg);
        var part = main.AddImagePart(type == "image/jpeg" ? ImagePartType.Jpeg : ImagePartType.Png);
        using (var ms = new MemoryStream(data)) part.FeedData(ms);
        string rid = main.GetIdOfPart(part);
        double w = ib.Box.Width, h = ib.Box.Height;
        if (w > contentWidth) { h *= contentWidth / w; w = contentWidth; }
        long cx = (long)(w * Emu), cy = (long)(h * Emu);
        uint myId = id++;
        var inline = new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            new DW.DocProperties { Id = myId, Name = $"Resim {myId}" },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = myId, Name = $"resim{myId}" }, new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = rid }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 };
        return new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = ((int)Math.Round(gapBefore * Twip * 0.9)).ToString(), After = "0" }), new Run(new Drawing(inline)));
    }
}
