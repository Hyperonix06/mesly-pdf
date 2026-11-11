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
        var fontCache = new Dictionary<nint, (string? Name, bool Bold, bool Italic)>();
        var buf = new byte[256];
        for (int k = 0; k < n; k++)
        {
            uint u = Native.FPDFText_GetUnicode(tp, k);
            if (Native.FPDFText_IsGenerated(tp, k) == 1) continue;
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
            if (!fontCache.TryGetValue(obj, out var fi))
            {
                string? name = null;
                fixed (byte* p = buf)
                {
                    var len = (int)Native.FPDFText_GetFontInfo(tp, k, p, new CULong((uint)buf.Length), out int flags).Value;
                    if (len > 1) name = Encoding.UTF8.GetString(buf, 0, Math.Min(len, buf.Length) - 1);
                    var (_, nb, ni) = TextLayout.ParseFontName(name);
                    fi = (name, nb || Native.FPDFText_GetFontWeight(tp, k) >= 600, ni || (flags & 64) != 0);
                }
                fontCache[obj] = fi;
            }
            uint rgb = Native.FPDFText_GetFillColor(tp, k, out var r, out var gg, out var b, out _) != 0 ? (r << 16) | (gg << 8) | b : 0;
            float angle = Native.FPDFText_GetCharAngle(tp, k);
            list.Add(new StyledChar(c, box, baseline, size > 0 ? size : box.Height, fi.Name, fi.Bold, fi.Italic, invisible ? 0 : rgb, invisible, angle));
        }
        return (IReadOnlyList<StyledChar>)list;
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
