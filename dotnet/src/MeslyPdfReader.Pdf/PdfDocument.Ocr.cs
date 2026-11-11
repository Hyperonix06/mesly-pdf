using System.Runtime.InteropServices;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf.Interop;

namespace MeslyPdfReader.Pdf;

/// <summary>Sayfadaki ana (taranmış) görüntü bilgisi.</summary>
public sealed record PageImageInfo(int ObjectIndex, RectF Bounds, int PixelWidth, int PixelHeight, bool IsJpeg, double CoverRatio);

/// <summary>Aşama 4 — OCR metin katmanı ve taranmış sayfa görüntüsü işlemleri.</summary>
public sealed unsafe partial class PdfDocument
{
    public const string MarkOcr = "MeslyOcr";

    /// <summary>
    /// Görüntü değiştiğinde JPEG olarak yeniden kodlamak için (uygulama WPF kodlayıcısını atar). Atanmamışsa
    /// görüntü kayıpsız (Flate) yazılır.
    /// </summary>
    public static Func<BgraImage, byte[]>? JpegEncoder { get; set; }

    /// <summary>Sayfada (OCR katmanı dışında) çıkarılabilir metin karakteri sayısı.</summary>
    public Task<int> CountRealTextCharsAsync(int pageIndex) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var tp = TextPage(pageIndex);
        int n = 0;
        foreach (var o in AllObjects(page))
        {
            if (Native.FPDFPageObj_GetType(o) != Native.FPDF_PAGEOBJ_TEXT || RoleOf(o) is MarkOcr or MarkWatermark or MarkHeaderFooter) continue;
            n += ReadObjText(o, tp).Count(c => !char.IsWhiteSpace(c));
        }
        return n;
    });

    /// <summary>Sayfada Mesly OCR katmanı var mı.</summary>
    public Task<bool> HasOcrLayerAsync(int pageIndex) => _engine.RunAsync(() => AllObjects(Page(pageIndex)).Any(o => RoleOf(o) == MarkOcr));

    /// <summary>
    /// OCR sonucunu sayfaya görünmez metin olarak yazar. <paramref name="ocr"/> sayfanın görüntülendiği yönde
    /// (sayfa döndürmesi uygulanmış) çizilmiş görüntüden elde edilmiş olmalıdır. Önceki Mesly OCR katmanı silinir.
    /// </summary>
    public Task<int> AddOcrLayerAsync(int pageIndex, OcrPage ocr, byte[] font) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        foreach (var o in AllObjects(page).Where(o => RoleOf(o) == MarkOcr).ToList())
            if (Native.FPDFPage_RemoveObject(page, o) != 0) Native.FPDFPageObj_Destroy(o);
        var f = LoadFont(font);
        Displayed(page, 0, 0, out var W, out var H);
        double sx = W / ocr.Width, sy = H / ocr.Height;
        int added = 0;
        // Satır başına tek yazı boyutu (en yüksek sözcük ≈ yazı tipi em yüksekliği): paragraf algılama tutarlı olsun.
        // Yazı boyutu: taban çizgisinden harf üstüne uzaklık (büyük harf/yükselen harf ≈ 0,74 em). Satırdaki sözcüklerin
        // %75'lik değeri kullanılır; yalnız küçük harfli sözcükler (x yüksekliği) tahmini aşağı çekmesin.
        var lineSize = ocr.Words.Where(w => w.Height > 0).GroupBy(w => w.Line).ToDictionary(g => g.Key, g =>
        {
            var hs = g.Select(w => (double)Math.Max(1, Math.Clamp(w.BaselineY, w.Top, w.Bottom) - w.Top)).OrderBy(h => h).ToList();
            double h = hs[(int)Math.Min(hs.Count - 1, Math.Floor(hs.Count * 0.75))];
            double em = h / 0.74;
            // Taban çizgisi bulunamadıysa kutu yüksekliğine dön.
            return (em > 1 ? em : g.Max(w => w.Height) * 0.9) * sy;
        });
        foreach (var w in ocr.Words)
        {
            if (w.Text.Length == 0 || w.Width <= 0 || w.Height <= 0) continue;
            double boxW = w.Width * sx;
            double size = Math.Clamp(lineSize[w.Line], 2, 400);
            double baseline = Math.Clamp(w.BaselineY, w.Top, w.Bottom);
            double dx = w.Left * sx, dy = (ocr.Height - baseline) * sy;
            var text = w.Text + " ";
            double measured = Measure(w.Text, f, size);
            double hscale = measured > 0.01 ? boxW / measured : 1;
            var (ux, uy, rot) = Displayed(page, dx, dy, out _, out _);
            var obj = Native.FPDFPageObj_CreateTextObj(_doc, f, (float)size);
            if (obj == 0) continue;
            fixed (char* t = text + "\0") Native.FPDFText_SetText(obj, (ushort*)t);
            Native.FPDFTextObj_SetTextRenderMode(obj, Native.FPDF_TEXTRENDERMODE_INVISIBLE);
            Native.FPDFPageObj_SetFillColor(obj, 0, 0, 0, 255);
            double a = rot * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
            Native.FPDFPageObj_Transform(obj, hscale * cos, hscale * sin, -sin, cos, ux, uy);
            AddMark(obj, MarkOcr);
            Native.FPDFPage_InsertObject(page, obj);
            added++;
        }
        Commit(pageIndex, page);
        return added;
    });

    public Task<int> RemoveOcrLayerAsync(IEnumerable<int> pages) => RemoveByRole(pages, MarkOcr);

    // ─────────────── Taranmış sayfa görüntüsü ───────────────

    /// <summary>Sayfayı en çok kaplayan görüntü nesnesi (kaplama oranı ≥ %50 değilse null).</summary>
    public Task<PageImageInfo?> GetMainImageAsync(int pageIndex) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var box = GetEffectiveBox(page);
        double pageArea = box.Width * box.Height;
        PageImageInfo? best = null;
        var objs = AllObjects(page).ToList();
        for (int i = 0; i < objs.Count; i++)
        {
            var o = objs[i];
            if (Native.FPDFPageObj_GetType(o) != Native.FPDF_PAGEOBJ_IMAGE) continue;
            var b = Bounds(o);
            double cover = Math.Max(0, Math.Min(b.Right, box.Right) - Math.Max(b.Left, box.Left)) * Math.Max(0, Math.Min(b.Top, box.Top) - Math.Max(b.Bottom, box.Bottom)) / pageArea;
            if (best is not null && cover <= best.CoverRatio) continue;
            Native.FPDFImageObj_GetImagePixelSize(o, out var pw, out var ph);
            best = new PageImageInfo(i, b, (int)pw, (int)ph, IsJpeg(o), cover);
        }
        return best is { CoverRatio: >= 0.5 } ? best : null;
    });

    private static bool IsJpeg(nint imageObj)
    {
        int n = Native.FPDFImageObj_GetImageFilterCount(imageObj);
        for (int k = 0; k < n; k++)
        {
            var buf = stackalloc byte[32];
            var len = Native.FPDFImageObj_GetImageFilter(imageObj, k, buf, new CULong(32u));
            if (len.Value > 1 && Marshal.PtrToStringAnsi((nint)buf) == "DCTDecode") return true;
        }
        return false;
    }

    /// <summary>Görüntü nesnesinin piksellerini (kendi çözünürlüğünde, BGRA) okur.</summary>
    public Task<BgraImage> GetImagePixelsAsync(int pageIndex, int objectIndex) => _engine.RunAsync(() =>
    {
        var o = Objects(Page(pageIndex), new[] { objectIndex })[0];
        if (Native.FPDFPageObj_GetType(o) != Native.FPDF_PAGEOBJ_IMAGE) throw new PdfException(PdfErrorKind.Unsupported, "Nesne görüntü değil.");
        return ReadBitmap(o);
    });

    private static BgraImage ReadBitmap(nint imageObj)
    {
        var bmp = Native.FPDFImageObj_GetBitmap(imageObj);
        if (bmp == 0) throw new PdfException(PdfErrorKind.Unsupported, "Görüntü verisi okunamadı (desteklenmeyen görüntü biçimi).");
        try
        {
            int w = Native.FPDFBitmap_GetWidth(bmp), h = Native.FPDFBitmap_GetHeight(bmp), stride = Native.FPDFBitmap_GetStride(bmp), fmt = Native.FPDFBitmap_GetFormat(bmp);
            var src = (byte*)Native.FPDFBitmap_GetBuffer(bmp);
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                byte* row = src + (long)y * stride;
                int o = y * w * 4;
                for (int x = 0; x < w; x++, o += 4)
                {
                    switch (fmt)
                    {
                        case Native.FPDFBitmap_Gray: px[o] = px[o + 1] = px[o + 2] = row[x]; px[o + 3] = 255; break;
                        case Native.FPDFBitmap_BGR: px[o] = row[x * 3]; px[o + 1] = row[x * 3 + 1]; px[o + 2] = row[x * 3 + 2]; px[o + 3] = 255; break;
                        default: px[o] = row[x * 4]; px[o + 1] = row[x * 4 + 1]; px[o + 2] = row[x * 4 + 2]; px[o + 3] = fmt == Native.FPDFBitmap_BGRA ? row[x * 4 + 3] : (byte)255; break;
                    }
                }
            }
            return new BgraImage { Width = w, Height = h, Stride = w * 4, Pixels = px };
        }
        finally { Native.FPDFBitmap_Destroy(bmp); }
    }

    /// <summary>
    /// Görüntü nesnesinin piksellerini değiştirir; nesnenin sayfadaki konumu korunur (boyut farklıysa aynı alana yayılır).
    /// Özgün görüntü JPEG ise ve kodlayıcı atanmışsa JPEG olarak yazılır.
    /// </summary>
    public Task SetImagePixelsAsync(int pageIndex, int objectIndex, BgraImage img) => _engine.RunAsync(() =>
    {
        var page = Page(pageIndex);
        var o = Objects(page, new[] { objectIndex })[0];
        WriteBitmap(page, o, img, IsJpeg(o));
        Commit(pageIndex, page);
    });

    private void WriteBitmap(nint page, nint o, BgraImage img, bool jpeg)
    {
        Native.FPDFPageObj_GetMatrix(o, out var m);
        var pages = stackalloc nint[1];
        pages[0] = page;
        if (jpeg && JpegEncoder is { } enc)
        {
            var data = enc(img);
            using var src = new StreamSource(new MemoryStream(data, false));
            if (Native.FPDFImageObj_LoadJpegFileInline(pages, 1, o, src.Access) == 0) throw new PdfException(PdfErrorKind.Format, "JPEG görüntüsü yazılamadı.");
        }
        else
        {
            var bgr = ToBgrOnWhite(img, out int stride);
            fixed (byte* p = bgr)
            {
                var bmp = Native.FPDFBitmap_CreateEx(img.Width, img.Height, Native.FPDFBitmap_BGR, p, stride);
                try { if (Native.FPDFImageObj_SetBitmap(pages, 1, o, bmp) == 0) throw new PdfException(PdfErrorKind.Unknown, "Görüntü yazılamadı."); }
                finally { Native.FPDFBitmap_Destroy(bmp); }
            }
        }
        Native.FPDFPageObj_SetMatrix(o, &m);
    }

    /// <summary>
    /// Görünmez (OCR) metnin altındaki görüntü piksellerini kâğıt rengiyle siler: taranmış sayfada metin
    /// düzenlendiğinde eski yazının görüntüde kalmaması için. Yalnızca eksenlere hizalı görüntülerde yapılır.
    /// </summary>
    private int EraseImageUnder(nint page, RectF area)
    {
        int erased = 0;
        foreach (var o in AllObjects(page).Where(o => Native.FPDFPageObj_GetType(o) == Native.FPDF_PAGEOBJ_IMAGE).ToList())
        {
            if (Native.FPDFPageObj_GetMatrix(o, out var m) == 0 || Math.Abs(m.b) > 1e-3 || Math.Abs(m.c) > 1e-3 || Math.Abs(m.a) < 1e-3 || Math.Abs(m.d) < 1e-3) continue;
            var b = Bounds(o);
            if (!Intersects(b, area)) continue;
            var img = ReadBitmap(o);
            // Sayfa → görüntü pikseli (y aşağı). Negatif ölçekler (ayna) için de doğru.
            double U(double x) => (x - m.e) / m.a * img.Width;
            double V(double y) => (1 - (y - m.f) / m.d) * img.Height;
            int x0 = (int)Math.Floor(Math.Min(U(area.Left), U(area.Right))), x1 = (int)Math.Ceiling(Math.Max(U(area.Left), U(area.Right)));
            int y0 = (int)Math.Floor(Math.Min(V(area.Bottom), V(area.Top))), y1 = (int)Math.Ceiling(Math.Max(V(area.Bottom), V(area.Top)));
            int pad = Math.Max(1, (y1 - y0) / 10);
            x0 -= pad; y0 -= pad; x1 += pad; y1 += pad;
            var (cb, cg, cr) = ImageOps.EstimatePaperColor(img, x0, y0, x1, y1);
            ImageOps.FillRect(img, x0, y0, x1, y1, cb, cg, cr);
            WriteBitmap(page, o, img, IsJpeg(o));
            erased++;
        }
        return erased;
    }
}
