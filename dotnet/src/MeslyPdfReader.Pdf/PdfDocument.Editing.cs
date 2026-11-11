using System.Runtime.InteropServices;
using System.Text;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf.Interop;

namespace MeslyPdfReader.Pdf;

/// <summary>
/// Aşama 3 — gerçek içerik düzenleme. Tüm işlemler sayfanın içerik nesneleri üzerinde yapılır
/// (nesne silinir / taşınır / yeniden oluşturulur); hiçbir değişiklik üstüne kutu çizerek taklit edilmez.
/// Her işlem sonunda sayfa içeriği yeniden üretilir (FPDFPage_GenerateContent).
/// </summary>
public sealed unsafe partial class PdfDocument
{
    public const string MarkWatermark = "MeslyWatermark", MarkHeaderFooter = "MeslyHeaderFooter",
        MarkBackground = "MeslyBackground", MarkUnderline = "MeslyUnderline", MarkArtifact = "Artifact";

    // ─────────────── Nesneleri okuma ───────────────

    public Task<IReadOnlyList<PageObjectInfo>> GetPageObjectsAsync(int pageIndex, CancellationToken ct = default) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var tp = TextPage(pageIndex);
        int n = Native.FPDFPage_CountObjects(page);
        var list = new List<PageObjectInfo>(n);
        for (int i = 0; i < n; i++)
        {
            var o = Native.FPDFPage_GetObject(page, i);
            if (o == 0) continue;
            list.Add(Describe(o, i, tp));
        }
        return (IReadOnlyList<PageObjectInfo>)list;
    }, ct);

    private PageObjectInfo Describe(nint o, int i, nint textPage)
    {
        var kind = (PageObjectKind)Native.FPDFPageObj_GetType(o);
        Native.FPDFPageObj_GetBounds(o, out var l, out var b, out var r, out var t);
        var bounds = new RectF(l, b, r, t).Normalize();
        uint fill = Native.FPDFPageObj_GetFillColor(o, out var fr, out var fg, out var fb, out _) != 0 ? (fr << 16) | (fg << 8) | fb : 0;
        uint stroke = Native.FPDFPageObj_GetStrokeColor(o, out var sr, out var sg, out var sb, out _) != 0 ? (sr << 16) | (sg << 8) | sb : 0;
        string? role = RoleOf(o);
        if (kind != PageObjectKind.Text) return new PageObjectInfo(i, kind, bounds, FillRgb: fill, StrokeRgb: stroke, MeslyRole: role);
        var text = ReadObjText(o, textPage);
        var (font, size, bold, italic) = FontInfo(o);
        bool invisible = Native.FPDFTextObj_GetTextRenderMode(o) == 3;
        return new PageObjectInfo(i, kind, bounds, text, font, size, fill, stroke, bold, italic, invisible, role);
    }

    private static string ReadObjText(nint o, nint textPage)
    {
        uint len = (uint)Native.FPDFTextObj_GetText(o, textPage, null, new CULong(0u)).Value;
        if (len <= 2) return "";
        var buf = new ushort[len / 2 + 1];
        fixed (ushort* p = buf) Native.FPDFTextObj_GetText(o, textPage, p, new CULong(len));
        var s = new string(buf.Select(c => (char)c).ToArray());
        int z = s.IndexOf('\0');
        return (z >= 0 ? s[..z] : s).Replace('￾', '-').Replace('\u0002', '-');
    }

    private static (string? Name, double Size, bool Bold, bool Italic) FontInfo(nint o)
    {
        var f = Native.FPDFTextObj_GetFont(o);
        string? name = null;
        if (f != 0)
        {
            nuint len = Native.FPDFFont_GetBaseFontName(f, null, 0);
            if (len > 1)
            {
                var buf = new byte[(int)len];
                fixed (byte* p = buf) Native.FPDFFont_GetBaseFontName(f, p, len);
                name = Encoding.UTF8.GetString(buf, 0, (int)len - 1);
            }
        }
        Native.FPDFTextObj_GetFontSize(o, out var size);
        double scale = 1;
        if (Native.FPDFPageObj_GetMatrix(o, out var m) != 0) scale = Math.Sqrt(m.c * m.c + m.d * m.d);
        var (_, nb, ni) = TextLayout.ParseFontName(name);
        bool bold = nb, italic = ni;
        if (f != 0)
        {
            if (Native.FPDFFont_GetWeight(f) >= 600) bold = true;
            if ((Native.FPDFFont_GetFlags(f) & 64) != 0) italic = true;
        }
        return (name, Math.Round(size * (scale > 0 ? scale : 1), 2), bold, italic);
    }

    private static string? RoleOf(nint o)
    {
        int n = Native.FPDFPageObj_CountMarks(o);
        for (int k = 0; k < n; k++)
        {
            var mark = Native.FPDFPageObj_GetMark(o, new CULong((uint)k));
            if (mark == 0) continue;
            var buf = stackalloc ushort[64];
            if (Native.FPDFPageObjMark_GetName(mark, buf, new CULong(128u), out var outLen) == 0) continue;
            int chars = Math.Max(0, (int)outLen.Value / 2 - 1);
            var name = new string((char*)buf, 0, Math.Min(chars, 63));
            if (name.StartsWith("Mesly", StringComparison.Ordinal)) return name;
        }
        return null;
    }

    /// <summary>Sayfadaki metinleri paragraflara gruplar (satırlar → paragraflar).</summary>
    public Task<IReadOnlyList<TextParagraph>> GetParagraphsAsync(int pageIndex, CancellationToken ct = default) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var tp = TextPage(pageIndex);
        int n = Native.FPDFPage_CountObjects(page);
        var items = new List<TextItem>();
        for (int i = 0; i < n; i++)
        {
            var o = Native.FPDFPage_GetObject(page, i);
            if (o == 0 || Native.FPDFPageObj_GetType(o) != Native.FPDF_PAGEOBJ_TEXT) continue;
            if (RoleOf(o) is MarkWatermark or MarkHeaderFooter) continue;
            var text = ReadObjText(o, tp);
            if (text.Length == 0) continue;
            Native.FPDFPageObj_GetBounds(o, out var l, out var b, out var r, out var t);
            Native.FPDFPageObj_GetMatrix(o, out var m);
            var (font, size, bold, italic) = FontInfo(o);
            Native.FPDFPageObj_GetFillColor(o, out var fr, out var fg, out var fb, out _);
            bool rotated = Math.Abs(m.b) > 0.01 * Math.Abs(m.a) || Math.Abs(m.c) > 0.01 * Math.Abs(m.d);
            items.Add(new TextItem(i, new RectF(l, b, r, t).Normalize(), text, font, size > 0 ? size : 10, (fr << 16) | (fg << 8) | fb, bold, italic, m.f, m.e, rotated));
        }
        return (IReadOnlyList<TextParagraph>)BuildParagraphs(items);
    }, ct);

    internal sealed record TextItem(int Index, RectF Box, string Text, string? Font, double Size, uint Rgb, bool Bold, bool Italic, double Baseline, double X, bool Rotated);

    internal static List<TextParagraph> BuildParagraphs(List<TextItem> items)
    {
        var result = new List<TextParagraph>();
        // 1) Satırlar
        var lines = new List<List<TextItem>>();
        foreach (var it in items.Where(i => !i.Rotated).OrderByDescending(i => Math.Round(i.Baseline, 1)).ThenBy(i => i.Box.Left))
        {
            var line = lines.FirstOrDefault(L =>
            {
                var last = L[^1];
                return Math.Abs(last.Baseline - it.Baseline) <= 0.3 * Math.Max(last.Size, it.Size)
                    && Math.Abs(last.Size - it.Size) <= 0.15 * last.Size
                    && it.Box.Left - L.Max(x => x.Box.Right) < 2.5 * it.Size
                    && it.Box.Left >= L[0].Box.Left - it.Size;
            });
            if (line is null) lines.Add(new List<TextItem> { it }); else line.Add(it);
        }
        foreach (var L in lines) L.Sort((a, b) => a.Box.Left.CompareTo(b.Box.Left));
        lines = lines.OrderByDescending(L => L[0].Baseline).ThenBy(L => L[0].Box.Left).ToList();

        // 2) Paragraflar
        var used = new bool[lines.Count];
        for (int s = 0; s < lines.Count; s++)
        {
            if (used[s]) continue;
            var group = new List<List<TextItem>> { lines[s] };
            used[s] = true;
            double step = -1;
            for (int k = s + 1; k < lines.Count; k++)
            {
                if (used[k]) continue;
                var prev = group[^1]; var cur = lines[k];
                double size = prev[0].Size;
                double dy = prev[0].Baseline - cur[0].Baseline;
                if (dy <= 0) continue;
                if (dy > 1.9 * size) break;
                bool sameStyle = Math.Abs(cur[0].Size - size) <= 0.15 * size && cur[0].Font == prev[0].Font;
                double pl = prev[0].Box.Left, cl = cur[0].Box.Left, pr = prev.Max(x => x.Box.Right), cr = cur.Max(x => x.Box.Right);
                bool overlapH = Math.Min(pr, cr) - Math.Max(pl, cl) > 0;
                bool consistent = step < 0 || Math.Abs(dy - step) <= 0.35 * step;
                if (!sameStyle || !overlapH || !consistent || dy < 0.8 * size) continue;
                group.Add(cur); used[k] = true; step = dy;
            }
            result.Add(MakeParagraph(group, step));
        }
        // 3) Döndürülmüş metin: her nesne kendi paragrafı
        foreach (var it in items.Where(i => i.Rotated))
            result.Add(new TextParagraph(new[] { it.Index }, it.Box, it.Text, it.Font, it.Size, it.Rgb, it.Bold, it.Italic, it.Baseline, it.Size * 1.2, TextAlign.Left));
        return result;
    }

    private static TextParagraph MakeParagraph(List<List<TextItem>> group, double step)
    {
        var sb = new StringBuilder();
        foreach (var line in group)
        {
            var ls = new StringBuilder();
            TextItem? prev = null;
            foreach (var it in line)
            {
                if (prev is not null && ls.Length > 0 && !ls.ToString().EndsWith(' ') && !it.Text.StartsWith(' ') && it.Box.Left - prev.Box.Right > 0.15 * it.Size)
                    ls.Append(' ');
                ls.Append(it.Text);
                prev = it;
            }
            var lineText = ls.ToString().TrimEnd();
            if (sb.Length > 0)
            {
                var cur = sb.ToString();
                if (cur.EndsWith('-') && lineText.Length > 0 && char.IsLower(lineText[0])) sb.Length--;
                else sb.Append(' ');
            }
            sb.Append(lineText.TrimStart());
        }
        var all = group.SelectMany(x => x).ToList();
        var bounds = all.Select(i => i.Box).Aggregate((a, b) => a.Union(b));
        var first = group[0][0];
        // Hizalama: sol kenarlar tutarlıysa sola, sağ kenarlar tutarlıysa sağa, merkezler tutarlıysa ortaya.
        var align = TextAlign.Left;
        if (group.Count >= 2)
        {
            var lefts = group.Select(L => L[0].Box.Left).ToList();
            var rights = group.Select(L => L.Max(x => x.Box.Right)).ToList();
            var centers = lefts.Zip(rights, (a, b) => (a + b) / 2).ToList();
            double tol = first.Size * 0.6;
            bool leftOk = lefts.Max() - lefts.Min() < tol, rightOk = rights.Max() - rights.Min() < tol, centerOk = centers.Max() - centers.Min() < tol;
            align = leftOk ? TextAlign.Left : centerOk ? TextAlign.Center : rightOk ? TextAlign.Right : TextAlign.Left;
        }
        var bold = all.Count(i => i.Bold) * 2 > all.Count;
        var italic = all.Count(i => i.Italic) * 2 > all.Count;
        return new TextParagraph(all.Select(i => i.Index).OrderBy(i => i).ToArray(), bounds, sb.ToString(), first.Font, first.Size, first.Rgb,
            bold, italic, first.Baseline, step > 0 ? step : first.Size * 1.2, align);
    }

    // ─────────────── Metin oluşturma / değiştirme ───────────────

    /// <summary>Verilen yazı tipiyle metnin genişliği (pt). PDFium ile ölçülür.</summary>
    public Task<double> MeasureTextAsync(string text, byte[] font, double size) => _engine.RunAsync(() => Measure(text, LoadFont(font), size));

    private double Measure(string text, nint font, double size)
    {
        if (text.Length == 0) return 0;
        var o = Native.FPDFPageObj_CreateTextObj(_doc, font, (float)size);
        try
        {
            fixed (char* t = text + "\0") Native.FPDFText_SetText(o, (ushort*)t);
            Native.FPDFPageObj_GetBounds(o, out var l, out _, out var r, out _);
            // Sondaki boşluk sınırlara girmez: yaklaşık genişlik ekle.
            double trailing = text.Length - text.TrimEnd().Length;
            return Math.Max(0, r - Math.Min(0, l)) + trailing * size * 0.28;
        }
        finally { Native.FPDFPageObj_Destroy(o); }
    }

    /// <summary>
    /// Paragrafı değiştirir: eski metin nesneleri sayfadan gerçekten silinir, yeni metin aynı konumdan başlayarak
    /// kutu genişliğine göre yeniden akıtılır. Döndürülen dikdörtgen yeni metnin kapladığı alandır.
    /// </summary>
    public Task<RectF> ReplaceParagraphAsync(int pageIndex, TextParagraph paragraph, string newText, TextStyle style, double? width = null) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        // Silinecek nesneleri, indeksleri kaymadan önce tutamak olarak topla.
        var targets = paragraph.ObjectIndices.Select(i => Native.FPDFPage_GetObject(page, i)).Where(o => o != 0).ToList();
        foreach (var o in AllObjects(page).Where(o => RoleOf(o) == MarkUnderline && Intersects(Bounds(o), paragraph.Bounds))) targets.Add(o);
        foreach (var o in targets.Distinct())
            if (Native.FPDFPage_RemoveObject(page, o) != 0) Native.FPDFPageObj_Destroy(o);
        double w = width ?? Math.Max(paragraph.Bounds.Width, style.Size * 4);
        var result = LayoutText(page, newText, style, paragraph.Bounds.Left, paragraph.FirstBaseline, w,
            style.LineSpacing > 0 ? style.Size * style.LineSpacing : paragraph.LineHeight);
        Commit(pageIndex, page);
        return result;
    });

    /// <summary>Yeni metin kutusu ekler (sol üst köşe + genişlik).</summary>
    public Task<RectF> AddTextBlockAsync(int pageIndex, double left, double top, double width, string text, TextStyle style) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var r = LayoutText(page, text, style, left, top - style.Size * 0.8, Math.Max(width, style.Size * 2), style.Size * style.LineSpacing);
        Commit(pageIndex, page);
        return r;
    });

    private RectF LayoutText(nint page, string text, TextStyle style, double left, double firstBaseline, double width, double lineHeight, string? mark = null, double angleDeg = 0)
    {
        var font = LoadFont(style.FontData);
        var lines = TextLayout.Wrap(text, width, s => Measure(s, font, style.Size));
        RectF total = default;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            double lw = Measure(line, font, style.Size);
            double x = style.Align switch { TextAlign.Center => left + (width - lw) / 2, TextAlign.Right => left + width - lw, _ => left };
            double y = firstBaseline - i * lineHeight;
            var o = CreateText(font, line, style, x, y, angleDeg, mark);
            Native.FPDFPage_InsertObject(page, o);
            var bounds = Bounds(o);
            total = total.IsEmpty ? bounds : total.Union(bounds);
            if (style.Underline)
            {
                double uy = y - style.Size * 0.12;
                var path = Native.FPDFPageObj_CreateNewPath((float)x, (float)uy);
                Native.FPDFPath_LineTo(path, (float)(x + lw), (float)uy);
                Native.FPDFPageObj_SetStrokeColor(path, (style.Rgb >> 16) & 0xFF, (style.Rgb >> 8) & 0xFF, style.Rgb & 0xFF, style.Alpha);
                Native.FPDFPageObj_SetStrokeWidth(path, (float)Math.Max(0.5, style.Size * 0.06));
                Native.FPDFPath_SetDrawMode(path, Native.FPDF_FILLMODE_NONE, 1);
                AddMark(path, MarkUnderline);
                Native.FPDFPage_InsertObject(page, path);
            }
        }
        return total;
    }

    private nint CreateText(nint font, string text, TextStyle style, double x, double y, double angleDeg, string? mark)
    {
        var o = Native.FPDFPageObj_CreateTextObj(_doc, font, (float)style.Size);
        if (o == 0) throw new PdfException(PdfErrorKind.Unknown, "Metin nesnesi oluşturulamadı.");
        fixed (char* t = text + "\0") Native.FPDFText_SetText(o, (ushort*)t);
        Native.FPDFPageObj_SetFillColor(o, (style.Rgb >> 16) & 0xFF, (style.Rgb >> 8) & 0xFF, style.Rgb & 0xFF, style.Alpha);
        double a = angleDeg * Math.PI / 180;
        Native.FPDFPageObj_Transform(o, Math.Cos(a), Math.Sin(a), -Math.Sin(a), Math.Cos(a), x, y);
        if (mark is not null) { AddMark(o, mark); AddMark(o, MarkArtifact); }
        return o;
    }

    private void AddMark(nint o, string name)
    {
        var n = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = n) Native.FPDFPageObj_AddMark(o, p);
    }

    private static RectF Bounds(nint o)
    {
        Native.FPDFPageObj_GetBounds(o, out var l, out var b, out var r, out var t);
        return new RectF(l, b, r, t).Normalize();
    }

    private static bool Intersects(RectF a, RectF b) => a.Left < b.Right && a.Right > b.Left && a.Bottom < b.Top && a.Top > b.Bottom;

    private static IEnumerable<nint> AllObjects(nint page)
    {
        int n = Native.FPDFPage_CountObjects(page);
        var list = new List<nint>(n);
        for (int i = 0; i < n; i++) { var o = Native.FPDFPage_GetObject(page, i); if (o != 0) list.Add(o); }
        return list;
    }

    private void Commit(int pageIndex, nint page)
    {
        if (Native.FPDFPage_GenerateContent(page) == 0) throw new PdfException(PdfErrorKind.Unknown, "Sayfa içeriği yazılamadı.");
        ClosePage(pageIndex);
        MarkChanged(structure: false);
    }

    // ─────────────── Nesne işlemleri ───────────────

    private List<nint> Objects(nint page, IEnumerable<int> indices)
    {
        var list = new List<nint>();
        foreach (var i in indices.Distinct())
        {
            var o = Native.FPDFPage_GetObject(page, i);
            if (o == 0) throw new PdfException(PdfErrorKind.Page, $"Nesne {i} bulunamadı (belge değişmiş olabilir).");
            list.Add(o);
        }
        return list;
    }

    public Task DeleteObjectsAsync(int pageIndex, IEnumerable<int> indices) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        foreach (var o in Objects(page, indices))
            if (Native.FPDFPage_RemoveObject(page, o) != 0) Native.FPDFPageObj_Destroy(o);
        Commit(pageIndex, page);
    });

    public Task MoveObjectsAsync(int pageIndex, IEnumerable<int> indices, double dx, double dy) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        foreach (var o in Objects(page, indices)) Native.FPDFPageObj_Transform(o, 1, 0, 0, 1, dx, dy);
        Commit(pageIndex, page);
    });

    /// <summary>Nesneyi yeni sınırlara ölçekler (görsel, şekil veya metin).</summary>
    public Task ResizeObjectAsync(int pageIndex, int index, RectF target, bool keepAspect = false) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var o = Objects(page, new[] { index })[0];
        var b = Bounds(o);
        if (b.Width < 0.01 || b.Height < 0.01) throw new PdfException(PdfErrorKind.Unsupported, "Bu nesne boyutlandırılamaz.");
        double sx = target.Width / b.Width, sy = target.Height / b.Height;
        if (keepAspect) sx = sy = Math.Min(sx, sy);
        Native.FPDFPageObj_Transform(o, sx, 0, 0, sy, target.Left - b.Left * sx, target.Bottom - b.Bottom * sy);
        Commit(pageIndex, page);
    });

    public Task AlignObjectsAsync(int pageIndex, IEnumerable<int> indices, AlignMode mode) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var objs = Objects(page, indices);
        if (objs.Count < 2) return;
        var boxes = objs.Select(Bounds).ToList();
        double l = boxes.Min(b => b.Left), r = boxes.Max(b => b.Right), bt = boxes.Min(b => b.Bottom), tp = boxes.Max(b => b.Top);
        for (int k = 0; k < objs.Count; k++)
        {
            var b = boxes[k];
            double dx = mode switch { AlignMode.Left => l - b.Left, AlignMode.Right => r - b.Right, AlignMode.CenterH => (l + r) / 2 - (b.Left + b.Right) / 2, _ => 0 };
            double dy = mode switch { AlignMode.Top => tp - b.Top, AlignMode.Bottom => bt - b.Bottom, AlignMode.Middle => (bt + tp) / 2 - (b.Bottom + b.Top) / 2, _ => 0 };
            if (dx != 0 || dy != 0) Native.FPDFPageObj_Transform(objs[k], 1, 0, 0, 1, dx, dy);
        }
        Commit(pageIndex, page);
    });

    /// <summary>Metin veya şeklin dolgu rengini değiştirir.</summary>
    public Task SetFillColorAsync(int pageIndex, IEnumerable<int> indices, uint rgb) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        foreach (var o in Objects(page, indices))
        {
            Native.FPDFPageObj_GetFillColor(o, out _, out _, out _, out var a);
            Native.FPDFPageObj_SetFillColor(o, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, a == 0 ? 255 : a);
        }
        Commit(pageIndex, page);
    });

    public Task SetStrokeAsync(int pageIndex, IEnumerable<int> indices, uint rgb, double width) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        foreach (var o in Objects(page, indices))
        {
            Native.FPDFPageObj_SetStrokeColor(o, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, 255);
            if (width > 0) Native.FPDFPageObj_SetStrokeWidth(o, (float)width);
        }
        Commit(pageIndex, page);
    });

    // ─────────────── Şekil ve görsel ───────────────

    public Task AddShapeAsync(int pageIndex, ShapeKind kind, RectF rect, ShapeStyle style) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var r = rect.Normalize();
        float L = (float)r.Left, B = (float)r.Bottom, R = (float)r.Right, T = (float)r.Top;
        nint path;
        switch (kind)
        {
            case ShapeKind.Rectangle:
                path = Native.FPDFPageObj_CreateNewRect(L, B, R - L, T - B);
                break;
            case ShapeKind.Ellipse:
                {
                    // Dört Bézier eğrisiyle elips (k = 0.5523).
                    float cx = (L + R) / 2, cy = (B + T) / 2, rx = (R - L) / 2, ry = (T - B) / 2, k = 0.5523f;
                    path = Native.FPDFPageObj_CreateNewPath(cx + rx, cy);
                    Native.FPDFPath_BezierTo(path, cx + rx, cy + k * ry, cx + k * rx, cy + ry, cx, cy + ry);
                    Native.FPDFPath_BezierTo(path, cx - k * rx, cy + ry, cx - rx, cy + k * ry, cx - rx, cy);
                    Native.FPDFPath_BezierTo(path, cx - rx, cy - k * ry, cx - k * rx, cy - ry, cx, cy - ry);
                    Native.FPDFPath_BezierTo(path, cx + k * rx, cy - ry, cx + rx, cy - k * ry, cx + rx, cy);
                    Native.FPDFPath_Close(path);
                    break;
                }
            default:
                {
                    // Çizgi / ok: rect.Left,Bottom → rect.Right,Top yönünde (Normalize öncesi yön korunur).
                    float x1 = (float)rect.Left, y1 = (float)rect.Bottom, x2 = (float)rect.Right, y2 = (float)rect.Top;
                    path = Native.FPDFPageObj_CreateNewPath(x1, y1);
                    Native.FPDFPath_LineTo(path, x2, y2);
                    if (kind == ShapeKind.Arrow)
                    {
                        double ang = Math.Atan2(y2 - y1, x2 - x1), len = Math.Max(6, style.StrokeWidth * 5);
                        for (int sgn = -1; sgn <= 1; sgn += 2)
                        {
                            double a2 = ang + Math.PI - sgn * 0.45;
                            Native.FPDFPath_MoveTo(path, x2, y2);
                            Native.FPDFPath_LineTo(path, (float)(x2 + len * Math.Cos(a2)), (float)(y2 + len * Math.Sin(a2)));
                        }
                    }
                    break;
                }
        }
        Native.FPDFPageObj_SetStrokeColor(path, (style.StrokeRgb >> 16) & 0xFF, (style.StrokeRgb >> 8) & 0xFF, style.StrokeRgb & 0xFF, style.Alpha);
        Native.FPDFPageObj_SetStrokeWidth(path, (float)style.StrokeWidth);
        bool fill = style.FillRgb is not null && kind is ShapeKind.Rectangle or ShapeKind.Ellipse;
        if (fill) Native.FPDFPageObj_SetFillColor(path, (style.FillRgb!.Value >> 16) & 0xFF, (style.FillRgb.Value >> 8) & 0xFF, style.FillRgb.Value & 0xFF, style.Alpha);
        Native.FPDFPath_SetDrawMode(path, fill ? Native.FPDF_FILLMODE_WINDING : Native.FPDF_FILLMODE_NONE, style.StrokeWidth > 0 ? 1 : 0);
        Native.FPDFPage_InsertObject(page, path);
        Commit(pageIndex, page);
    });

    /// <summary>Görsel ekler; görsel <paramref name="rect"/> içine orantılı sığdırılır.</summary>
    public Task<RectF> AddImageAsync(int pageIndex, ImagePageSource img, RectF rect) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var obj = Native.FPDFPageObj_NewImageObj(_doc);
        try
        {
            var (w, h) = FillImage(obj, img, page);
            var target = Fit(rect.Normalize(), w, h);
            Native.FPDFImageObj_SetMatrix(obj, target.Width, 0, 0, target.Height, target.Left, target.Bottom);
            Native.FPDFPage_InsertObject(page, obj);
            obj = 0;
            Commit(pageIndex, page);
            return target;
        }
        finally { if (obj != 0) Native.FPDFPageObj_Destroy(obj); }
    });

    /// <summary>Mevcut görselin içeriğini değiştirir; yeni görsel eskisinin alanına orantılı sığdırılır.</summary>
    public Task ReplaceImageAsync(int pageIndex, int index, ImagePageSource img) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var o = Objects(page, new[] { index })[0];
        if (Native.FPDFPageObj_GetType(o) != Native.FPDF_PAGEOBJ_IMAGE) throw new PdfException(PdfErrorKind.Unsupported, "Seçili nesne bir görsel değil.");
        var old = Bounds(o);
        var (w, h) = FillImage(o, img, page);
        var target = Fit(old, w, h);
        Native.FPDFImageObj_SetMatrix(o, target.Width, 0, 0, target.Height, target.Left, target.Bottom);
        Commit(pageIndex, page);
    });

    private static RectF Fit(RectF box, int pxW, int pxH)
    {
        double s = Math.Min(box.Width / pxW, box.Height / pxH);
        double w = pxW * s, h = pxH * s;
        double x = box.Left + (box.Width - w) / 2, y = box.Bottom + (box.Height - h) / 2;
        return new RectF(x, y, x + w, y + h);
    }

    private (int W, int H) FillImage(nint obj, ImagePageSource img, nint page)
    {
        var pages = stackalloc nint[1];
        pages[0] = page;
        switch (img)
        {
            case JpegImageSource j:
                using (var src = new StreamSource(new MemoryStream(j.Data, false)))
                    if (Native.FPDFImageObj_LoadJpegFileInline(pages, 1, obj, src.Access) == 0) throw new PdfException(PdfErrorKind.Format, "JPEG görüntüsü okunamadı.");
                Native.FPDFImageObj_GetImagePixelSize(obj, out var w, out var h);
                return ((int)w, (int)h);
            case BitmapImageSource b:
                var bgr = ToBgrOnWhite(b.Image, out int stride);
                fixed (byte* p = bgr)
                {
                    var bmp = Native.FPDFBitmap_CreateEx(b.Image.Width, b.Image.Height, Native.FPDFBitmap_BGR, p, stride);
                    try { if (Native.FPDFImageObj_SetBitmap(pages, 1, obj, bmp) == 0) throw new PdfException(PdfErrorKind.Unknown, "Görüntü eklenemedi."); }
                    finally { Native.FPDFBitmap_Destroy(bmp); }
                }
                return (b.Image.Width, b.Image.Height);
            default: throw new ArgumentException("Bilinmeyen görüntü türü.");
        }
    }

    // ─────────────── Bağlantılar ───────────────

    public Task AddLinkAsync(int pageIndex, RectF rect, string uri) => _engine.RunAsync(() =>
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "mailto"))
            throw new PdfException(PdfErrorKind.Unsupported, "Bağlantı http://, https:// veya mailto: ile başlamalı.");
        var page = Page(pageIndex);
        var annot = Native.FPDFPage_CreateAnnot(page, Native.FPDF_ANNOT_LINK);
        if (annot == 0) throw new PdfException(PdfErrorKind.Unknown, "Bağlantı oluşturulamadı.");
        try
        {
            var r = rect.Normalize();
            var fr = new FS_RECTF { left = (float)r.Left, bottom = (float)r.Bottom, right = (float)r.Right, top = (float)r.Top };
            Native.FPDFAnnot_SetRect(annot, &fr);
            Native.FPDFAnnot_SetBorder(annot, 0, 0, 0);
            var bytes = Encoding.ASCII.GetBytes(u.AbsoluteUri + "\0");
            fixed (byte* p = bytes) if (Native.FPDFAnnot_SetURI(annot, p) == 0) throw new PdfException(PdfErrorKind.Unknown, "Bağlantı adresi yazılamadı.");
        }
        finally { Native.FPDFPage_CloseAnnot(annot); }
        ClosePage(pageIndex);
        MarkChanged(structure: false);
    });

    /// <summary>Noktadaki bağlantı açıklamasını (annotation) sayfadan kaldırır. Kaldırıldıysa true.</summary>
    public Task<bool> RemoveLinkAtAsync(int pageIndex, double x, double y) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        int n = Native.FPDFPage_GetAnnotCount(page);
        for (int i = n - 1; i >= 0; i--)
        {
            var a = Native.FPDFPage_GetAnnot(page, i);
            if (a == 0) continue;
            bool hit = false;
            try
            {
                if (Native.FPDFAnnot_GetSubtype(a) == Native.FPDF_ANNOT_LINK && Native.FPDFAnnot_GetRect(a, out var r) != 0)
                    hit = new RectF(r.left, r.bottom, r.right, r.top).Normalize().Contains(x, y);
            }
            finally { Native.FPDFPage_CloseAnnot(a); }
            if (hit && Native.FPDFPage_RemoveAnnot(page, i) != 0)
            {
                ClosePage(pageIndex);
                MarkChanged(structure: false);
                return true;
            }
        }
        return false;
    });

    // ─────────────── Filigran, üst/altbilgi, arka plan ───────────────

    /// <summary>Görüntülenen yöndeki (X sağa, Y yukarı, pt) bir noktayı kullanıcı alanına çevirir; döndürme açısını verir.</summary>
    private static (double X, double Y, double Angle) Displayed(nint page, double dispX, double dispY, out double dispW, out double dispH)
    {
        var box = GetEffectiveBox(page);
        int rot = Native.FPDFPage_GetRotation(page);
        var g = new PageGeometry(box, rot);
        bool swap = rot % 2 == 1;
        dispW = swap ? box.Height : box.Width; dispH = swap ? box.Width : box.Height;
        var (x, y) = g.FromUnit(dispX / dispW, 1 - dispY / dispH);
        return (x, y, rot * 90.0);
    }

    public Task AddWatermarkAsync(IEnumerable<int> pages, WatermarkSettings s) => _engine.RunAsync(() =>
    {
        var arr = Validate(pages, PageCount);
        var font = LoadFont(s.Style.FontData);
        foreach (var i in arr)
        {
            var page = Page(i);
            Displayed(page, 0, 0, out var W, out var H);
            double size = s.Style.Size > 0 ? s.Style.Size : Math.Max(12, Math.Sqrt(W * W + H * H) * 0.65 / Math.Max(1, Measure(s.Text, font, 1)));
            double tw = Measure(s.Text, font, size), th = size * 0.7;
            double a = s.AngleDegrees * Math.PI / 180;
            // Metnin merkezi sayfa merkezine gelsin.
            double ox = W / 2 - (tw / 2 * Math.Cos(a) - th / 2 * Math.Sin(a));
            double oy = H / 2 - (tw / 2 * Math.Sin(a) + th / 2 * Math.Cos(a));
            var (ux, uy, rotAngle) = Displayed(page, ox, oy, out _, out _);
            var style = s.Style with { Size = size, Alpha = (byte)Math.Clamp(s.Opacity * 255, 10, 255) };
            var o = CreateText(font, s.Text, style, ux, uy, s.AngleDegrees + rotAngle, MarkWatermark);
            Native.FPDFPage_InsertObject(page, o);
            Commit(i, page);
        }
    });

    /// <summary>Mesly ile eklenmiş (veya standart “Artifact” filigranı olarak işaretlenmiş) filigranları siler. Silinen nesne sayısı.</summary>
    public Task<int> RemoveWatermarksAsync(IEnumerable<int> pages) => RemoveByRole(pages, MarkWatermark);
    public Task<int> RemoveHeaderFooterAsync(IEnumerable<int> pages) => RemoveByRole(pages, MarkHeaderFooter);
    public Task<int> RemoveBackgroundAsync(IEnumerable<int> pages) => RemoveByRole(pages, MarkBackground);

    private Task<int> RemoveByRole(IEnumerable<int> pages, string role) => _engine.RunAsync(() =>
    {
        var arr = Validate(pages, PageCount);
        int removed = 0;
        foreach (var i in arr)
        {
            var page = Page(i);
            var victims = AllObjects(page).Where(o => RoleOf(o) == role).ToList();
            foreach (var o in victims) if (Native.FPDFPage_RemoveObject(page, o) != 0) { Native.FPDFPageObj_Destroy(o); removed++; }
            if (victims.Count > 0) Commit(i, page);
        }
        return removed;
    });

    public Task AddHeaderFooterAsync(IEnumerable<int> pages, HeaderFooterSettings s) => _engine.RunAsync(() =>
    {
        var arr = Validate(pages, PageCount);
        var font = LoadFont(s.Style.FontData);
        var now = DateTime.Now;
        int total = arr.Length;
        for (int k = 0; k < arr.Length; k++)
        {
            int i = arr[k];
            var page = Page(i);
            Displayed(page, 0, 0, out var W, out var H);
            foreach (var item in s.Items)
            {
                var text = TextLayout.Fill(item.Template, s.StartNumber + k, s.StartNumber + total - 1, s.FileName, now);
                if (string.IsNullOrWhiteSpace(text)) continue;
                double tw = Measure(text, font, s.Style.Size);
                double x = item.Position switch
                {
                    HFPosition.TopLeft or HFPosition.BottomLeft => s.MarginPt,
                    HFPosition.TopRight or HFPosition.BottomRight => W - s.MarginPt - tw,
                    _ => (W - tw) / 2,
                };
                bool top = item.Position is HFPosition.TopLeft or HFPosition.TopCenter or HFPosition.TopRight;
                double y = top ? H - s.MarginPt - s.Style.Size * 0.8 : s.MarginPt;
                var (ux, uy, rot) = Displayed(page, x, y, out _, out _);
                var o = CreateText(font, text, s.Style, ux, uy, rot, MarkHeaderFooter);
                Native.FPDFPage_InsertObject(page, o);
            }
            Commit(i, page);
        }
    });

    /// <summary>Sayfanın en altına (tüm içeriğin arkasına) renkli arka plan ekler.</summary>
    public Task SetBackgroundAsync(IEnumerable<int> pages, uint rgb) => _engine.RunAsync(() =>
    {
        var arr = Validate(pages, PageCount);
        foreach (var i in arr)
        {
            var page = Page(i);
            foreach (var old in AllObjects(page).Where(o => RoleOf(o) == MarkBackground).ToList())
                if (Native.FPDFPage_RemoveObject(page, old) != 0) Native.FPDFPageObj_Destroy(old);
            var box = GetEffectiveBox(page);
            var rect = Native.FPDFPageObj_CreateNewRect((float)box.Left, (float)box.Bottom, (float)box.Width, (float)box.Height);
            Native.FPDFPageObj_SetFillColor(rect, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, 255);
            Native.FPDFPath_SetDrawMode(rect, Native.FPDF_FILLMODE_WINDING, 0);
            AddMark(rect, MarkBackground);
            AddMark(rect, MarkArtifact);
            Native.FPDFPage_InsertObjectAtIndex(page, rect, 0);
            Commit(i, page);
        }
    });
}
