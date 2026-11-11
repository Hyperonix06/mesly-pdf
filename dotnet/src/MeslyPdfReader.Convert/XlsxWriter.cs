using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MeslyPdfReader.Core;

namespace MeslyPdfReader.Convert;

/// <summary>
/// Düzen modelinden Excel çalışma kitabı yazar. Tablolar hücrelere birebir; tablo dışındaki metin satırları
/// geniş boşluklardan sütunlara bölünür. Sayılar (Türkçe “1.234,56” ve “1234.56” biçimleri) sayı olarak yazılır.
/// </summary>
public static partial class XlsxWriter
{
    public sealed record Options(bool OneSheetPerPage = true, bool TablesOnly = false);

    public static void Write(string path, IReadOnlyList<PageLayout> pages, Options? o = null)
    {
        o ??= new Options();
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wb = doc.AddWorkbookPart();
        wb.Workbook = new Workbook();
        AddStyles(wb);
        var sheets = wb.Workbook.AppendChild(new Sheets());
        uint sheetId = 1;
        var groups = o.OneSheetPerPage ? pages.Select(p => (Name: $"Sayfa {p.PageIndex + 1}", Pages: (IReadOnlyList<PageLayout>)new[] { p }))
            : new[] { (Name: "Belge", Pages: pages) };
        foreach (var (name, pgs) in groups)
        {
            var rows = new List<List<(string Text, bool Header)>>();
            var merges = new List<string>();
            foreach (var page in pgs)
            {
                foreach (var b in page.Blocks)
                {
                    switch (b)
                    {
                        case TableBlock tb:
                            if (rows.Count > 0) rows.Add(new());
                            for (int r = 0; r < tb.Rows; r++)
                            {
                                var row = new List<(string, bool)>();
                                foreach (var cell in tb.Cells.Where(c => c.Row == r).OrderBy(c => c.Col))
                                {
                                    while (row.Count < cell.Col) row.Add(("", false));
                                    if (cell.ColSpan > 1) merges.Add($"{Ref(row.Count, rows.Count)}:{Ref(row.Count + cell.ColSpan - 1, rows.Count)}");
                                    row.Add((cell.Text, r == 0));
                                    for (int k = 1; k < cell.ColSpan; k++) row.Add(("", r == 0));
                                }
                                rows.Add(row);
                            }
                            rows.Add(new());
                            break;
                        case ParagraphBlock pb when !o.TablesOnly:
                            rows.Add(SplitColumns(pb).Select(t => (t, false)).ToList());
                            break;
                    }
                }
            }
            var ws = wb.AddNewPart<WorksheetPart>();
            var data = new SheetData();
            int maxCols = rows.Count == 0 ? 1 : Math.Max(1, rows.Max(r => r.Count));
            var widths = new double[maxCols];
            for (int r = 0; r < rows.Count; r++)
            {
                var row = new Row { RowIndex = (uint)(r + 1) };
                for (int c = 0; c < rows[r].Count; c++)
                {
                    var (text, header) = rows[r][c];
                    if (text.Length == 0) continue;
                    widths[c] = Math.Max(widths[c], Math.Min(60, text.Split('\n').Max(s => s.Length)));
                    row.Append(MakeCell(Ref(c, r), text, header));
                }
                data.Append(row);
            }
            var cols = new Columns();
            for (int c = 0; c < maxCols; c++)
                cols.Append(new Column { Min = (uint)(c + 1), Max = (uint)(c + 1), Width = Math.Max(8, widths[c] + 2), CustomWidth = true });
            ws.Worksheet = new Worksheet(cols, data);
            if (merges.Count > 0)
            {
                var mc = new MergeCells();
                foreach (var m in merges) mc.Append(new MergeCell { Reference = m });
                ws.Worksheet.InsertAfter(mc, data);
            }
            sheets.Append(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = sheetId++, Name = name.Length > 31 ? name[..31] : name });
        }
        wb.Workbook.Save();
    }

    /// <summary>Bir paragraf satırını geniş boşluklardan sütunlara böler (ör. “Ürün      Adet      Tutar”).</summary>
    internal static List<string> SplitColumns(ParagraphBlock pb)
    {
        var text = pb.Text;
        var parts = BigGap().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        return parts.Count == 0 ? new List<string> { text } : parts;
    }

    [GeneratedRegex(@"\s{3,}|\t")]
    private static partial Regex BigGap();

    private static Cell MakeCell(string reference, string text, bool header)
    {
        if (TryNumber(text, out var number))
            return new Cell { CellReference = reference, DataType = CellValues.Number, CellValue = new CellValue(number), StyleIndex = header ? 1u : 0u };
        return new Cell
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(text) { Space = SpaceProcessingModeValues.Preserve }),
            StyleIndex = header ? 1u : (text.Contains('\n') ? 2u : 0u),
        };
    }

    /// <summary>“1.234,56”, “-12,5”, “1234.56”, “%15”, “12,5 TL” biçimlerini sayıya çevirir (yüzde ve para birimi atılır).</summary>
    public static bool TryNumber(string s, out double value)
    {
        value = 0;
        var t = s.Trim().Replace(" ", " ");
        bool percent = false;
        if (t.StartsWith('%')) { percent = true; t = t[1..].Trim(); }
        else if (t.EndsWith('%')) { percent = true; t = t[..^1].Trim(); }
        foreach (var cur in new[] { "TL", "₺", "€", "$", "USD", "EUR" })
            if (t.EndsWith(cur, StringComparison.OrdinalIgnoreCase)) { t = t[..^cur.Length].Trim(); break; }
            else if (t.StartsWith(cur, StringComparison.OrdinalIgnoreCase)) { t = t[cur.Length..].Trim(); break; }
        if (t.Length == 0 || t.Length > 20) return false;
        string norm;
        if (TrNumber().IsMatch(t)) norm = t.Replace(".", "").Replace(',', '.');
        else if (PlainNumber().IsMatch(t)) norm = t;
        else return false;
        if (!double.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        if (percent) value /= 100;
        return true;
    }

    // Türkçe: binlik nokta, ondalık virgül (1.234.567,89 · 12,5 · 1.250)
    [GeneratedRegex(@"^-?(\d{1,3}(\.\d{3})+|\d+)(,\d+)?$")]
    private static partial Regex TrNumber();
    [GeneratedRegex(@"^-?\d+\.\d+$")]
    private static partial Regex PlainNumber();

    private static string Ref(int col, int row)
    {
        string c = "";
        for (int n = col + 1; n > 0; n = (n - 1) / 26) c = (char)('A' + (n - 1) % 26) + c;
        return c + (row + 1);
    }

    private static void AddStyles(WorkbookPart wb)
    {
        var sp = wb.AddNewPart<WorkbookStylesPart>();
        sp.Stylesheet = new Stylesheet(
            new Fonts(new Font(new FontSize { Val = 11 }, new FontName { Val = "Calibri" }),
                      new Font(new Bold(), new FontSize { Val = 11 }, new FontName { Val = "Calibri" })),
            new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })),
            new Borders(new Border()),
            new CellFormats(
                new CellFormat { FontId = 0, FillId = 0, BorderId = 0 },
                new CellFormat { FontId = 1, FillId = 0, BorderId = 0, ApplyFont = true },
                new CellFormat(new Alignment { WrapText = true, Vertical = VerticalAlignmentValues.Top }) { FontId = 0, ApplyAlignment = true }));
    }
}
