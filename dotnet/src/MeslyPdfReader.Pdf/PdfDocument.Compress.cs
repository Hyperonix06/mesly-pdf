using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf.Interop;

namespace MeslyPdfReader.Pdf;

/// <summary>PDF sıkıştırma seçenekleri.</summary>
public sealed record CompressOptions(int TargetDpi = 150, int JpegQuality = 75, bool Grayscale = false)
{
    public static CompressOptions High => new(220, 85);
    public static CompressOptions Medium => new(150, 75);
    public static CompressOptions Small => new(100, 60);
}

/// <summary>Sıkıştırma sonucu.</summary>
public sealed record CompressReport(int ImagesSeen, int ImagesRecompressed, long BytesBefore, long BytesAfter, bool Written)
{
    public double Ratio => BytesBefore == 0 ? 1 : (double)BytesAfter / BytesBefore;
}

/// <summary>Aşama 5 — görselleri hedef çözünürlüğe küçültüp yeniden kodlayarak PDF'yi küçültür.</summary>
public sealed unsafe partial class PdfDocument
{
    /// <summary>
    /// Kalite parametreli JPEG kodlayıcı (uygulama WPF ile atar). Atanmamışsa yalnız küçültme yapılır, görüntü kayıpsız yazılır.
    /// </summary>
    public static Func<BgraImage, int, bool, byte[]>? JpegEncoderWithQuality { get; set; }

    /// <summary>
    /// Sayfalardaki görselleri yeniden örnekler: etkin çözünürlüğü hedefin %25 üstündeyse hedefe küçültülür, JPEG olarak
    /// yeniden kodlanır. Küçük görseller ve maskeli (saydam) görseller dokunulmadan bırakılır. Belge kaydedilince küçülür.
    /// </summary>
    public Task<(int Seen, int Changed)> CompressImagesAsync(CompressOptions o, IProgress<double>? progress = null, CancellationToken ct = default) => _engine.RunAsync(() =>
    {
        int seen = 0, changed = 0;
        for (int p = 0; p < PageCount; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = Page(p);
            bool pageChanged = false;
            foreach (var obj in AllObjects(page).Where(x => Native.FPDFPageObj_GetType(x) == Native.FPDF_PAGEOBJ_IMAGE).ToList())
            {
                seen++;
                var b = Bounds(obj);
                Native.FPDFImageObj_GetImagePixelSize(obj, out var pw, out var ph);
                if (pw < 64 || ph < 64 || b.Width < 1 || b.Height < 1) continue;
                double dpi = Math.Max(pw / (b.Width / 72), ph / (b.Height / 72));
                bool tooDense = dpi > o.TargetDpi * 1.25;
                var filters = ImageFilters(obj);
                // Siyah-beyaz tarama süzgeçleri zaten çok küçüktür; JPEG'e çevirmek büyütür ve bulanıklaştırır.
                if (filters.Contains("CCITTFaxDecode") || filters.Contains("JBIG2Decode")) continue;
                bool jpeg = filters.Contains("DCTDecode");
                // Kayıpsız (Flate) büyük görsel → JPEG'e çevirmek de yer kazandırır (fotoğraf/tarama).
                bool lossless = !jpeg && (long)pw * ph > 250_000 && JpegEncoderWithQuality is not null;
                if (!tooDense && !lossless && !o.Grayscale) continue;
                BgraImage img;
                try { img = ReadBitmap(obj); } catch { continue; }
                if (IsMasked(page, obj)) continue; // maskeli (saydam) görsel: JPEG saydamlığı taşıyamaz
                if (tooDense)
                {
                    double s = o.TargetDpi / dpi;
                    img = Resample(img, Math.Max(1, (int)Math.Round(img.Width * s)), Math.Max(1, (int)Math.Round(img.Height * s)));
                }
                if (o.Grayscale) img = ImageOps.FromGray(ImageOps.ToGray(img));
                Native.FPDFPageObj_GetMatrix(obj, out var m);
                var pages = stackalloc nint[1];
                pages[0] = page;
                if (JpegEncoderWithQuality is { } enc)
                {
                    var data = enc(img, o.JpegQuality, o.Grayscale);
                    using var src = new StreamSource(new MemoryStream(data, false));
                    if (Native.FPDFImageObj_LoadJpegFileInline(pages, 1, obj, src.Access) == 0) continue;
                }
                else
                {
                    var bgr = ToBgrOnWhite(img, out int stride);
                    fixed (byte* px = bgr)
                    {
                        var bmp = Native.FPDFBitmap_CreateEx(img.Width, img.Height, Native.FPDFBitmap_BGR, px, stride);
                        try { if (Native.FPDFImageObj_SetBitmap(pages, 1, obj, bmp) == 0) continue; }
                        finally { Native.FPDFBitmap_Destroy(bmp); }
                    }
                }
                Native.FPDFPageObj_SetMatrix(obj, &m);
                changed++; pageChanged = true;
            }
            if (pageChanged)
            {
                GenerateContent(p, page);
                ClosePage(p);
            }
            progress?.Report((p + 1.0) / PageCount);
        }
        if (changed > 0) MarkChanged(structure: false);
        return (seen, changed);
    }, ct);

    /// <summary>Görsel, maskesi uygulanmış çizimde saydam piksel içeriyor mu (SMask, Mask, stencil).</summary>
    private bool IsMasked(nint page, nint obj)
    {
        var bmp = Native.FPDFImageObj_GetRenderedBitmap(_doc, page, obj);
        if (bmp == 0) return true; // denetlenemiyorsa dokunma
        try
        {
            if (Native.FPDFBitmap_GetFormat(bmp) != Native.FPDFBitmap_BGRA) return false;
            int w = Native.FPDFBitmap_GetWidth(bmp), h = Native.FPDFBitmap_GetHeight(bmp), stride = Native.FPDFBitmap_GetStride(bmp);
            var buf = (byte*)Native.FPDFBitmap_GetBuffer(bmp);
            int step = Math.Max(1, Math.Min(w, h) / 200);
            for (int y = 0; y < h; y += step)
            {
                byte* row = buf + (long)y * stride;
                for (int x = 0; x < w; x += step) if (row[x * 4 + 3] < 250) return true;
            }
            return false;
        }
        finally { Native.FPDFBitmap_Destroy(bmp); }
    }

    /// <summary>Alan ortalamalı küçültme (kaliteli, takma yok).</summary>
    internal static BgraImage Resample(BgraImage src, int w, int h)
    {
        if (w >= src.Width && h >= src.Height) return src;
        var dst = new byte[w * h * 4];
        double sx = (double)src.Width / w, sy = (double)src.Height / h;
        Parallel.For(0, h, y =>
        {
            int y0 = (int)(y * sy), y1 = Math.Max(y0 + 1, (int)((y + 1) * sy));
            for (int x = 0; x < w; x++)
            {
                int x0 = (int)(x * sx), x1 = Math.Max(x0 + 1, (int)((x + 1) * sx));
                long b = 0, g = 0, r = 0, a = 0; int n = 0;
                for (int yy = y0; yy < y1 && yy < src.Height; yy++)
                {
                    int row = yy * src.Stride;
                    for (int xx = x0; xx < x1 && xx < src.Width; xx++)
                    {
                        int o = row + xx * 4;
                        b += src.Pixels[o]; g += src.Pixels[o + 1]; r += src.Pixels[o + 2]; a += src.Pixels[o + 3]; n++;
                    }
                }
                int d = (y * w + x) * 4;
                if (n == 0) n = 1;
                dst[d] = (byte)(b / n); dst[d + 1] = (byte)(g / n); dst[d + 2] = (byte)(r / n); dst[d + 3] = (byte)(a / n);
            }
        });
        return new BgraImage { Width = w, Height = h, Stride = w * 4, Pixels = dst };
    }
}
