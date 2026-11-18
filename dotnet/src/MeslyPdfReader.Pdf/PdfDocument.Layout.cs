using System.Runtime.InteropServices;
using System.Text;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf.Interop;

namespace MeslyPdfReader.Pdf;

/// <summary>Aşama 5 — dönüştürme için biçimli karakterler ve sayfa görselleri (görüntülenen yönde, pt, sol-üst başlangıç).</summary>
public sealed unsafe partial class PdfDocument
{
    /// <summary>
    /// Sayfadaki karakterleri biçim bilgisiyle döndürür. Koordinatlar sayfanın görüntülendiği yönde (döndürme uygulanmış),
    /// pt cinsinden, sol-üst köşe başlangıçlıdır. Filigran ve üst/altbilgi gibi Mesly yapay öğeleri atlanır.
    /// </summary>
    public Task<IReadOnlyList<StyledChar>> GetStyledCharsAsync(int pageIndex, CancellationToken ct = default) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var tp = TextPage(pageIndex);
        var g = new PageGeometry(GetEffectiveBox(page), Native.FPDFPage_GetRotation(page));
        var (dw, dh) = DisplaySize(page);
        int n = Math.Max(0, Native.FPDFText_CountChars(tp));
        var list = new List<StyledChar>(n);
        var fontCache = new Dictionary<nint, (string? Name, bool Bold, bool Italic, int Flags)>();
        var scaleCache = new Dictionary<nint, double>();
        var buf = new byte[256];
        for (int k = 0; k < n; k++)
        {
            uint u = Native.FPDFText_GetUnicode(tp, k);
            if (Native.FPDFText_IsGenerated(tp, k) == 1)
            {
                // pdfium'un ürettiği sözcük aralığı boşluğu: PDF'te boşluk karakteri olmayan (LaTeX, Google Docs) metinde sözcük sınırı bilgisidir.
                if (u == 0x20 && list.Count > 0)
                {
                    var pv = list[^1];
                    list.Add(pv with { Char = ' ', Box = new Box(pv.Box.X1, pv.Box.Y0, pv.Box.X1, pv.Box.Y1) });
                }
                continue;
            }
            char c = u switch { 0 => ' ', 0x02 or 0xFFFE => '-', <= 0xFFFF => (char)u, _ => '�' };
            if (c is '\r' or '\n') continue;
            if (Native.FPDFText_GetLooseCharBox(tp, k, out var rc) == 0) continue;
            var obj = Native.FPDFText_GetTextObject(tp, k);
            if (obj != 0 && RoleOf(obj) is MarkWatermark or MarkHeaderFooter) continue;
            bool invisible = obj != 0 && Native.FPDFTextObj_GetTextRenderMode(obj) == 3;
            var box = ToDisplay(g, dw, dh, new RectF(rc.left, rc.bottom, rc.right, rc.top).Normalize());
            Native.FPDFText_GetCharOrigin(tp, k, out var ox, out var oy);
            var (bx, by) = g.ToUnit(ox, oy);
            double baseline = by * dh;
            double size = Native.FPDFText_GetFontSize(tp, k);
            // Yazı tipi boyutu metin nesnesinin matrisiyle (içerik akışındaki cm/Tm ölçeği; Google Docs 0,75 kullanır) çarpılmalı.
            if (obj != 0)
            {
                if (!scaleCache.TryGetValue(obj, out var sc))
                {
                    sc = Native.FPDFPageObj_GetMatrix(obj, out var mt) != 0 ? Math.Sqrt(mt.c * mt.c + mt.d * mt.d) : 1.0;
                    if (sc < 1e-3 || double.IsNaN(sc)) sc = 1.0;
                    scaleCache[obj] = sc;
                }
                size *= sc;
            }
            if (!fontCache.TryGetValue(obj, out var fi))
            {
                string? name = null;
                fixed (byte* p = buf)
                {
                    var len = (int)Native.FPDFText_GetFontInfo(tp, k, p, new CULong((uint)buf.Length), out int flags).Value;
                    if (len > 1) name = Encoding.UTF8.GetString(buf, 0, Math.Min(len, buf.Length) - 1);
                    var (_, nb, ni) = TextLayout.ParseFontName(name);
                    fi = (name, nb || Native.FPDFText_GetFontWeight(tp, k) >= 600, ni || (flags & 64) != 0, flags);
                }
                fontCache[obj] = fi;
            }
            uint rgb = Native.FPDFText_GetFillColor(tp, k, out var r, out var gg, out var b, out _) != 0 ? (r << 16) | (gg << 8) | b : 0;
            float angle = Native.FPDFText_GetCharAngle(tp, k);
            list.Add(new StyledChar(c, box, baseline, size > 0 ? size : box.Height, fi.Name, fi.Bold, fi.Italic, invisible ? 0 : rgb, invisible, angle, fi.Flags));
        }
        return (IReadOnlyList<StyledChar>)list;
    }, ct);

    /// <summary>
    /// Sayfadaki çizgi benzeri vektör nesneleri (çizilmiş çizgiler, ince dolu dikdörtgenler, çerçeve/hücre kenarları) pt cinsinden döndürür.
    /// Görüntü işleme gerektirmez; tablo kenarlarını doğrudan PDF'ten verir. Beyaz/saydam çizgiler ve eğri yollar atlanır.
    /// </summary>
    public Task<IReadOnlyList<VectorLine>> GetVectorLinesAsync(int pageIndex, CancellationToken ct = default) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var g = new PageGeometry(GetEffectiveBox(page), Native.FPDFPage_GetRotation(page));
        var (dw, dh) = DisplaySize(page);
        var res = new List<VectorLine>();
        int count = Native.FPDFPage_CountObjects(page);
        for (int oi = 0; oi < count; oi++)
        {
            if ((oi & 63) == 0) ct.ThrowIfCancellationRequested();
            var o = Native.FPDFPage_GetObject(page, oi);
            if (o == 0 || Native.FPDFPageObj_GetType(o) != Native.FPDF_PAGEOBJ_PATH) continue;
            if (Native.FPDFPath_GetDrawMode(o, out int fill, out int stroke) == 0) continue;
            bool strokeOn = stroke != 0, fillOn = fill != 0;
            if (!strokeOn && !fillOn) continue;
            bool Visible(bool useStroke)
            {
                uint r, gg, b, a;
                if ((useStroke ? Native.FPDFPageObj_GetStrokeColor(o, out r, out gg, out b, out a) : Native.FPDFPageObj_GetFillColor(o, out r, out gg, out b, out a)) == 0) return true;
                return a > 20 && (0.299 * r + 0.587 * gg + 0.114 * b) < 235;
            }
            if (strokeOn && !Visible(true)) strokeOn = false;
            if (fillOn && !Visible(false)) fillOn = false;
            if (!strokeOn && !fillOn) continue;
            double scale = 1;
            double m_a = 1, m_b = 0, m_c = 0, m_d = 1, m_e = 0, m_f = 0;
            if (Native.FPDFPageObj_GetMatrix(o, out var mt) != 0) { m_a = mt.a; m_b = mt.b; m_c = mt.c; m_d = mt.d; m_e = mt.e; m_f = mt.f; scale = Math.Sqrt(Math.Abs(m_a * m_d - m_b * m_c)); if (scale < 1e-6) scale = 1; }
            double sw = 0.75;
            if (Native.FPDFPageObj_GetStrokeWidth(o, out var swf) != 0 && swf > 0) sw = swf * scale;
            int ns = Native.FPDFPath_CountSegments(o);
            if (ns < 2 || ns > 400) continue;
            // Alt yollar: her MOVETO yeni alt yol; BEZIER varsa nesne atlanır (eğri şekil).
            var subs = new List<(List<(double X, double Y)> Pts, bool Closed)>();
            List<(double X, double Y)>? cur = null; bool closed = false, curved = false;
            for (int si = 0; si < ns && !curved; si++)
            {
                var seg = Native.FPDFPath_GetPathSegment(o, si);
                if (seg == 0) continue;
                int type = Native.FPDFPathSegment_GetType(seg);
                if (type == 1) { curved = true; break; }
                Native.FPDFPathSegment_GetPoint(seg, out var px, out var py);
                double ux = m_a * px + m_c * py + m_e, uy = m_b * px + m_d * py + m_f;
                var (u, v) = g.ToUnit(ux, uy);
                if (type == 2) { if (cur is not null) subs.Add((cur, closed)); cur = new() { (u * dw, v * dh) }; closed = false; }
                else { cur ??= new(); cur.Add((u * dw, v * dh)); }
                if (Native.FPDFPathSegment_GetClose(seg) != 0) closed = true;
            }
            if (curved) continue;
            if (cur is not null) subs.Add((cur, closed));
            foreach (var (pts0, isClosed0) in subs)
            {
                var pts = pts0; bool isClosed = isClosed0;
                if (pts.Count >= 5 && Math.Abs(pts[0].X - pts[^1].X) < 0.3 && Math.Abs(pts[0].Y - pts[^1].Y) < 0.3) { pts = pts.Take(pts.Count - 1).ToList(); isClosed = true; }
                // Eksene hizalı dikdörtgen (4 köşe)
                if (pts.Count == 4 && (isClosed || fillOn))
                {
                    double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
                    bool axis = pts.All(p => (Math.Abs(p.X - x0) < 0.3 || Math.Abs(p.X - x1) < 0.3) && (Math.Abs(p.Y - y0) < 0.3 || Math.Abs(p.Y - y1) < 0.3));
                    if (axis)
                    {
                        double w = x1 - x0, h = y1 - y0;
                        if (fillOn && !strokeOn)
                        {
                            // Dolu ince dikdörtgen = çizgi; büyük dolu dikdörtgen = zemin rengi, çizgi değil.
                            if (h <= 2.5 && w >= 4) res.Add(new VectorLine(true, (y0 + y1) / 2, x0, x1, Math.Max(h, 0.5)));
                            else if (w <= 2.5 && h >= 4) res.Add(new VectorLine(false, (x0 + x1) / 2, y0, y1, Math.Max(w, 0.5)));
                        }
                        else if (strokeOn)
                        {
                            if (w >= 4) { res.Add(new VectorLine(true, y0, x0, x1, sw)); if (h > 1) res.Add(new VectorLine(true, y1, x0, x1, sw)); }
                            if (h >= 4) { res.Add(new VectorLine(false, x0, y0, y1, sw)); if (w > 1) res.Add(new VectorLine(false, x1, y0, y1, sw)); }
                        }
                        continue;
                    }
                }
                if (!strokeOn) continue;
                for (int k = 1; k < pts.Count + (isClosed ? 1 : 0); k++)
                {
                    var p = pts[k - 1]; var q = pts[k % pts.Count];
                    double dx = Math.Abs(q.X - p.X), dy = Math.Abs(q.Y - p.Y);
                    if (dy < 0.3 && dx >= 4) res.Add(new VectorLine(true, (p.Y + q.Y) / 2, Math.Min(p.X, q.X), Math.Max(p.X, q.X), sw));
                    else if (dx < 0.3 && dy >= 4) res.Add(new VectorLine(false, (p.X + q.X) / 2, Math.Min(p.Y, q.Y), Math.Max(p.Y, q.Y), sw));
                }
            }
        }
        return (IReadOnlyList<VectorLine>)res;
    }, ct);

    private static (double W, double H) DisplaySize(nint page)
    {
        var box = GetEffectiveBox(page);
        bool swap = Native.FPDFPage_GetRotation(page) % 2 == 1;
        return swap ? (box.Height, box.Width) : (box.Width, box.Height);
    }

    /// <summary>Kullanıcı alanı dikdörtgeni → görüntülenen yön (pt, sol-üst başlangıç, y aşağı).</summary>
    private static Box ToDisplay(PageGeometry g, double dw, double dh, RectF r)
    {
        var (u1, v1) = g.ToUnit(r.Left, r.Bottom);
        var (u2, v2) = g.ToUnit(r.Right, r.Top);
        return new Box(Math.Min(u1, u2) * dw, Math.Min(v1, v2) * dh, Math.Max(u1, u2) * dw, Math.Max(v1, v2) * dh);
    }

    /// <summary>Sayfanın görüntülenen boyutu (pt).</summary>
    public Task<SizeF> GetDisplaySizeAsync(int pageIndex) => _engine.RunAsync(() =>
    {
        var (w, h) = DisplaySize(Page(pageIndex));
        return new SizeF(w, h);
    });

    /// <summary>Sayfadaki görselleri piksel verisiyle (görüntülenen yönde konum) döndürür. Çok küçük görseller atlanır.</summary>
    public Task<IReadOnlyList<PageImage>> GetPageImagesAsync(int pageIndex, double minSizePt = 12, CancellationToken ct = default) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var g = new PageGeometry(GetEffectiveBox(page), Native.FPDFPage_GetRotation(page));
        var (dw, dh) = DisplaySize(page);
        var list = new List<PageImage>();
        var objs = AllObjects(page).ToList();
        for (int i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            if (Native.FPDFPageObj_GetType(o) != Native.FPDF_PAGEOBJ_IMAGE) continue;
            var b = Bounds(o);
            if (b.Width < minSizePt || b.Height < minSizePt) continue;
            BgraImage px;
            // Görseli sayfada göründüğü yönde ve maskesi uygulanmış haliyle al.
            var bmp = Native.FPDFImageObj_GetRenderedBitmap(_doc, page, o);
            if (bmp != 0)
            {
                try { px = CopyBitmap(bmp); }
                finally { Native.FPDFBitmap_Destroy(bmp); }
            }
            else
            {
                try { px = ReadBitmap(o); } catch { continue; }
            }
            int rot = Native.FPDFPage_GetRotation(page);
            if (rot != 0) px = ImageOps.RotateQuarter(px, rot);
            list.Add(new PageImage(i, ToDisplay(g, dw, dh, b), px, IsJpeg(o)));
        }
        return (IReadOnlyList<PageImage>)list;
    }, ct);

    private static BgraImage CopyBitmap(nint bmp)
    {
        int w = Native.FPDFBitmap_GetWidth(bmp), h = Native.FPDFBitmap_GetHeight(bmp), stride = Native.FPDFBitmap_GetStride(bmp), fmt = Native.FPDFBitmap_GetFormat(bmp);
        var src = (byte*)Native.FPDFBitmap_GetBuffer(bmp);
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            byte* row = src + (long)y * stride;
            for (int x = 0, o = y * w * 4; x < w; x++, o += 4)
            {
                if (fmt == Native.FPDFBitmap_Gray) { px[o] = px[o + 1] = px[o + 2] = row[x]; px[o + 3] = 255; }
                else if (fmt == Native.FPDFBitmap_BGR) { px[o] = row[x * 3]; px[o + 1] = row[x * 3 + 1]; px[o + 2] = row[x * 3 + 2]; px[o + 3] = 255; }
                else
                {
                    byte a = fmt == Native.FPDFBitmap_BGRA ? row[x * 4 + 3] : (byte)255;
                    // Saydam pikselleri beyaz zemine yerleştir (Word/Excel'de siyah görünmesin).
                    px[o] = (byte)((row[x * 4] * a + 255 * (255 - a)) / 255);
                    px[o + 1] = (byte)((row[x * 4 + 1] * a + 255 * (255 - a)) / 255);
                    px[o + 2] = (byte)((row[x * 4 + 2] * a + 255 * (255 - a)) / 255);
                    px[o + 3] = 255;
                }
            }
        }
        return new BgraImage { Width = w, Height = h, Stride = w * 4, Pixels = px };
    }
}
