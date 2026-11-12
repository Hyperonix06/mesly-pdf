using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using MeslyPdfReader.Core;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace MeslyPdfReader.Convert;

/// <summary>
/// Düzen modelinden düzenlenebilir PowerPoint sunusu yazar: her PDF sayfası bir slayt (slayt boyutu sayfa boyutu),
/// paragraflar özgün konumlarında biçimli metin kutuları, çizgili tablolar PowerPoint tablosu, görseller resim olarak.
/// </summary>
public static class PptxWriter
{
    private const long Emu = 12700; // 1 pt
    private static long E(double pt) => (long)Math.Round(pt * Emu);

    public static void Write(string path, IReadOnlyList<PageLayout> pages, string? title = null)
    {
        if (pages.Count == 0) throw new ArgumentException("Dönüştürülecek sayfa yok.", nameof(pages));
        using var doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var pres = doc.AddPresentationPart();
        pres.Presentation = new Presentation();

        // Slayt boyutu: ilk sayfanın boyutu (PowerPoint tek slayt boyutunu destekler; diğer sayfalar oranı korunarak sığdırılır).
        double sw = pages[0].Width, sh = pages[0].Height;
        long cx = Math.Clamp(E(sw), 914400, 51206400), cy = Math.Clamp(E(sh), 914400, 51206400);

        var (master, layout) = CreateMaster(pres);
        var ids = new SlideIdList();
        uint slideId = 256;
        foreach (var page in pages)
        {
            var sp = pres.AddNewPart<SlidePart>();
            sp.AddPart(layout);
            double scale = Math.Min(cx / (double)E(page.Width), cy / (double)E(page.Height));
            double offX = (cx - E(page.Width) * scale) / 2 / Emu, offY = (cy - E(page.Height) * scale) / 2 / Emu;
            sp.Slide = BuildSlide(sp, page, scale, offX, offY);
            sp.Slide.Save();
            ids.Append(new SlideId { Id = slideId++, RelationshipId = pres.GetIdOfPart(sp) });
        }

        pres.Presentation.Append(
            new SlideMasterIdList(new SlideMasterId { Id = 2147483648U, RelationshipId = pres.GetIdOfPart(master) }),
            ids,
            new SlideSize { Cx = (int)cx, Cy = (int)cy },
            new NotesSize { Cx = 6858000, Cy = 9144000 },
            new DefaultTextStyle());
        pres.Presentation.Save();
        if (title is not null)
        {
            doc.PackageProperties.Title = title;
            doc.PackageProperties.Creator = "Mesly PDF Reader";
        }
    }

    // ─────────────── Slayt içeriği ───────────────

    private static Slide BuildSlide(SlidePart part, PageLayout page, double scale, double offX, double offY)
    {
        var tree = new ShapeTree(
            new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1U, Name = "" }, new P.NonVisualGroupShapeDrawingProperties(), new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(new A.TransformGroup()));
        uint id = 2;
        Box Map(Box b) => new(offX + b.X0 * scale, offY + b.Y0 * scale, offX + b.X1 * scale, offY + b.Y1 * scale);
        foreach (var block in page.Blocks)
        {
            switch (block)
            {
                case ImageBlock ib: tree.Append(Picture(part, ib, Map(ib.Box), id++)); break;
                case TableBlock tb: tree.Append(Table(tb, Map(tb.Box), scale, id++)); break;
                case ParagraphBlock pb when pb.Text.Trim().Length > 0: tree.Append(TextBox(pb, Map(pb.Box), scale, id++)); break;
            }
        }
        return new Slide(new CommonSlideData(tree), new ColorMapOverride(new A.MasterColorMapping()));
    }

    private static P.Shape TextBox(ParagraphBlock pb, Box box, double scale, uint id)
    {
        // Yazı tipi farkından taşmasın diye kutu biraz geniş tutulur; metin kaydırılır.
        double w = Math.Max(box.Width * 1.04 + 2, 8), h = Math.Max(box.Height, 6);
        return new P.Shape(
            new P.NonVisualShapeProperties(new P.NonVisualDrawingProperties { Id = id, Name = $"Metin {id}" },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }) { TextBox = true }, new ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = E(box.X0), Y = E(box.Y0) }, new A.Extents { Cx = E(w), Cy = E(h) }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                new A.NoFill()),
            new P.TextBody(
                new A.BodyProperties(new A.NoAutoFit()) { Wrap = A.TextWrappingValues.Square, LeftInset = 0, RightInset = 0, TopInset = 0, BottomInset = 0, Anchor = A.TextAnchoringTypeValues.Top },
                new A.ListStyle(),
                Paragraph(pb, scale)));
    }

    private static A.Paragraph Paragraph(ParagraphBlock pb, double scale)
    {
        var ppr = new A.ParagraphProperties
        {
            Alignment = pb.Align switch { ParaAlign.Center => A.TextAlignmentTypeValues.Center, ParaAlign.Right => A.TextAlignmentTypeValues.Right, ParaAlign.Justify => A.TextAlignmentTypeValues.Justified, _ => A.TextAlignmentTypeValues.Left },
        };
        // Satır aralığı: PDF satır adımı / (boyut × 1,2) → yüzde.
        int pct = (int)Math.Round(Math.Clamp(pb.LineSpacing / 1.2, 0.8, 3) * 100000);
        ppr.Append(new A.LineSpacing(new A.SpacingPercent { Val = pct }));
        var p = new A.Paragraph(ppr);
        foreach (var r in pb.Runs)
        {
            if (r.Text.Length == 0) continue;
            p.Append(Run(r, scale));
        }
        p.Append(new A.EndParagraphRunProperties { Language = "tr-TR", FontSize = Size(pb.Runs.FirstOrDefault()?.Size ?? 11, scale) });
        return p;
    }

    private static int Size(double pt, double scale) => (int)Math.Round(Math.Clamp(pt * scale, 1, 400) * 100);

    private static A.Run Run(TextRun r, double scale)
    {
        var rpr = new A.RunProperties { Language = "tr-TR", FontSize = Size(r.Size, scale), Bold = r.Bold, Italic = r.Italic, Dirty = false };
        rpr.Append(new A.SolidFill(new A.RgbColorModelHex { Val = $"{r.Rgb & 0xFFFFFF:X6}" }));
        if (r.FontFamily is { Length: > 0 } f) rpr.Append(new A.LatinFont { Typeface = f }, new A.ComplexScriptFont { Typeface = f });
        return new A.Run(rpr, new A.Text(r.Text));
    }

    private static P.GraphicFrame Table(TableBlock tb, Box box, double scale, uint id)
    {
        var grid = new A.TableGrid();
        for (int c = 0; c < tb.Cols; c++) grid.Append(new A.GridColumn { Width = E((tb.ColEdges[c + 1] - tb.ColEdges[c]) * scale) });
        var table = new A.Table(new A.TableProperties { FirstRow = false, BandRow = false }, grid);
        for (int r = 0; r < tb.Rows; r++)
        {
            var row = new A.TableRow { Height = E((tb.RowEdges[r + 1] - tb.RowEdges[r]) * scale) };
            int col = 0;
            foreach (var cell in tb.Cells.Where(c => c.Row == r).OrderBy(c => c.Col))
            {
                // Eksik hücreleri boş hücreyle doldur (tablo ızgarası tam olmalı).
                while (col < cell.Col) { row.Append(Cell(null, 1, scale)); col++; }
                row.Append(Cell(cell, cell.ColSpan, scale));
                for (int k = 1; k < cell.ColSpan; k++) row.Append(new A.TableCell(EmptyBody(), new A.TableCellProperties()) { HorizontalMerge = true });
                col += Math.Max(1, cell.ColSpan);
            }
            while (col < tb.Cols) { row.Append(Cell(null, 1, scale)); col++; }
            table.Append(row);
        }
        return new P.GraphicFrame(
            new P.NonVisualGraphicFrameProperties(new P.NonVisualDrawingProperties { Id = id, Name = $"Tablo {id}" },
                new P.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoGrouping = true }), new ApplicationNonVisualDrawingProperties()),
            new Transform(new A.Offset { X = E(box.X0), Y = E(box.Y0) }, new A.Extents { Cx = E(box.Width), Cy = E(box.Height) }),
            new A.Graphic(new A.GraphicData(table) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" }));
    }

    private static A.TextBody EmptyBody() => new(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.EndParagraphRunProperties { Language = "tr-TR" }));

    private static A.TableCell Cell(TableCell? cell, int span, double scale)
    {
        A.TextBody body;
        if (cell is null || cell.Content.Count == 0) body = EmptyBody();
        else
        {
            body = new A.TextBody(new A.BodyProperties(), new A.ListStyle());
            foreach (var p in cell.Content) body.Append(Paragraph(p, scale));
        }
        var line = () => new A.SolidFill(new A.RgbColorModelHex { Val = "000000" });
        var tcpr = new A.TableCellProperties(
            new A.LeftBorderLineProperties(line()) { Width = 6350 }, new A.RightBorderLineProperties(line()) { Width = 6350 },
            new A.TopBorderLineProperties(line()) { Width = 6350 }, new A.BottomBorderLineProperties(line()) { Width = 6350 },
            new A.NoFill())
        { LeftMargin = 45720, RightMargin = 45720, TopMargin = 22860, BottomMargin = 22860 };
        var tc = new A.TableCell(body, tcpr);
        if (span > 1) tc.GridSpan = span;
        return tc;
    }

    private static P.Picture Picture(SlidePart part, ImageBlock ib, Box box, uint id)
    {
        var (data, type) = DocxWriter.ImageEncoder(ib.Image, ib.PreferJpeg);
        var img = part.AddImagePart(type == "image/jpeg" ? ImagePartType.Jpeg : ImagePartType.Png);
        using (var ms = new MemoryStream(data)) img.FeedData(ms);
        return new P.Picture(
            new P.NonVisualPictureProperties(new P.NonVisualDrawingProperties { Id = id, Name = $"Resim {id}" },
                new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }), new ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(new A.Blip { Embed = part.GetIdOfPart(img) }, new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = E(box.X0), Y = E(box.Y0) }, new A.Extents { Cx = E(box.Width), Cy = E(box.Height) }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
    }

    // ─────────────── Asgari ana slayt, düzen ve tema ───────────────

    private static (SlideMasterPart, SlideLayoutPart) CreateMaster(PresentationPart pres)
    {
        var master = pres.AddNewPart<SlideMasterPart>();
        var layout = master.AddNewPart<SlideLayoutPart>();
        layout.SlideLayout = new SlideLayout(
            new CommonSlideData(EmptyTree()) { Name = "Boş" },
            new ColorMapOverride(new A.MasterColorMapping()))
        { Type = SlideLayoutValues.Blank, Preserve = true };
        layout.AddPart(master);

        var theme = master.AddNewPart<ThemePart>();
        theme.Theme = Theme();
        pres.AddPart(theme);

        master.SlideMaster = new SlideMaster(
            new CommonSlideData(new Background(new BackgroundStyleReference(new A.SchemeColor { Val = A.SchemeColorValues.Background1 }) { Index = 1001U }), EmptyTree()),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1, Text1 = A.ColorSchemeIndexValues.Dark1, Background2 = A.ColorSchemeIndexValues.Light2, Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1, Accent2 = A.ColorSchemeIndexValues.Accent2, Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4, Accent5 = A.ColorSchemeIndexValues.Accent5, Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink, FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink,
            },
            new SlideLayoutIdList(new SlideLayoutId { Id = 2147483649U, RelationshipId = master.GetIdOfPart(layout) }),
            new TextStyles(new TitleStyle(), new BodyStyle(), new OtherStyle()));
        layout.SlideLayout.Save();
        master.SlideMaster.Save();
        return (master, layout);
    }

    private static ShapeTree EmptyTree() => new(
        new P.NonVisualGroupShapeProperties(new P.NonVisualDrawingProperties { Id = 1U, Name = "" }, new P.NonVisualGroupShapeDrawingProperties(), new ApplicationNonVisualDrawingProperties()),
        new GroupShapeProperties(new A.TransformGroup()));

    private static A.Theme Theme()
    {
        A.SolidFill Fill(A.SchemeColorValues v) => new(new A.SchemeColor { Val = v });
        A.Outline Line(int w) => new(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = w };
        return new A.Theme(
            new A.ThemeElements(
                new A.ColorScheme(
                    new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
                    new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
                    new A.Dark2Color(new A.RgbColorModelHex { Val = "1F2937" }),
                    new A.Light2Color(new A.RgbColorModelHex { Val = "F3F4F6" }),
                    new A.Accent1Color(new A.RgbColorModelHex { Val = "D91315" }),
                    new A.Accent2Color(new A.RgbColorModelHex { Val = "1B2A4A" }),
                    new A.Accent3Color(new A.RgbColorModelHex { Val = "A5A5A5" }),
                    new A.Accent4Color(new A.RgbColorModelHex { Val = "FFC000" }),
                    new A.Accent5Color(new A.RgbColorModelHex { Val = "4472C4" }),
                    new A.Accent6Color(new A.RgbColorModelHex { Val = "70AD47" }),
                    new A.Hyperlink(new A.RgbColorModelHex { Val = "0563C1" }),
                    new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "954F72" }))
                { Name = "Mesly" },
                new A.FontScheme(
                    new A.MajorFont(new A.LatinFont { Typeface = "Calibri Light" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" }),
                    new A.MinorFont(new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" }))
                { Name = "Mesly" },
                new A.FormatScheme(
                    new A.FillStyleList(Fill(A.SchemeColorValues.PhColor), Fill(A.SchemeColorValues.PhColor), Fill(A.SchemeColorValues.PhColor)),
                    new A.LineStyleList(Line(6350), Line(12700), Line(19050)),
                    new A.EffectStyleList(new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList())),
                    new A.BackgroundFillStyleList(Fill(A.SchemeColorValues.PhColor), Fill(A.SchemeColorValues.PhColor), Fill(A.SchemeColorValues.PhColor)))
                { Name = "Mesly" }),
            new A.ObjectDefaults(),
            new A.ExtraColorSchemeList())
        { Name = "Mesly" };
    }
}
