using System.Runtime.InteropServices;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf.Interop;

namespace MeslyPdfReader.Pdf;

public abstract record ImagePageSource(double DpiX, double DpiY);
/// <summary>JPEG verisi olduğu gibi (yeniden sıkıştırmadan, kalite kaybı olmadan) gömülür.</summary>
public sealed record JpegImageSource(byte[] Data, double DpiX = 96, double DpiY = 96) : ImagePageSource(DpiX, DpiY);
public sealed record BitmapImageSource(BgraImage Image, double DpiX = 96, double DpiY = 96) : ImagePageSource(DpiX, DpiY);

/// <param name="Paper">null: sayfa görüntü boyutunda; değilse görüntü kâğıda sığdırılıp ortalanır.</param>
public sealed record ImagePageLayout(SizeF? Paper = null, double MarginPt = 0, bool AutoRotate = true);

public sealed unsafe partial class PdfDocument
{
    /// <summary>Sayfaya gerçek bir PDF metin nesnesi ekler (yazı tipi gömülür, Türkçe karakterler korunur).</summary>
    public Task AddTextAsync(int pageIndex, string text, double x, double y, double fontSize, byte[] trueTypeFont, uint rgb = 0x000000)
        => _engine.RunAsync(() =>
        {
            ThrowIfDisposed();
            var page = Page(pageIndex);
            var font = LoadFont(trueTypeFont);
            var obj = Native.FPDFPageObj_CreateTextObj(_doc, font, (float)fontSize);
            if (obj == 0) throw new PdfException(PdfErrorKind.Unknown, "Metin nesnesi oluşturulamadı.");
            fixed (char* t = text + "\0")
                if (Native.FPDFText_SetText(obj, (ushort*)t) == 0) { Native.FPDFPageObj_Destroy(obj); throw new PdfException(PdfErrorKind.Unknown, "Metin ayarlanamadı."); }
            Native.FPDFPageObj_SetFillColor(obj, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, 255);
            Native.FPDFPageObj_Transform(obj, 1, 0, 0, 1, x, y);
            Native.FPDFPage_InsertObject(page, obj);
            GenerateContent(pageIndex, page);
            ClosePage(pageIndex);
            MarkChanged(structure: false);
        });

    /// <summary>Dolgulu dikdörtgen ekler.</summary>
    public Task AddRectangleAsync(int pageIndex, RectF rect, uint rgb) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        var page = Page(pageIndex);
        var obj = Native.FPDFPageObj_CreateNewRect((float)rect.Left, (float)rect.Bottom, (float)rect.Width, (float)rect.Height);
        Native.FPDFPageObj_SetFillColor(obj, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, 255);
        Native.FPDFPath_SetDrawMode(obj, 1, 0);
        Native.FPDFPage_InsertObject(page, obj);
        GenerateContent(pageIndex, page);
        ClosePage(pageIndex);
        MarkChanged(structure: false);
    });

    private nint LoadFont(byte[] ttf)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(ttf));
        if (_fonts.TryGetValue(key, out var f)) return f;
        fixed (byte* p = ttf) f = Native.FPDFText_LoadFont(_doc, p, (uint)ttf.Length, Native.FPDF_FONT_TRUETYPE, 1);
        if (f == 0) throw new PdfException(PdfErrorKind.Unsupported, "Yazı tipi yüklenemedi.");
        _fonts[key] = f;
        return f;
    }


    public Task AppendImagePageAsync(ImagePageSource img, ImagePageLayout layout) => _engine.RunAsync(() =>
    {
        ThrowIfDisposed();
        var obj = Native.FPDFPageObj_NewImageObj(_doc);
        if (obj == 0) throw new PdfException(PdfErrorKind.Unknown, "Görüntü nesnesi oluşturulamadı.");
        int pxW, pxH;
        try
        {
            switch (img)
            {
                case JpegImageSource j:
                    using (var src = new StreamSource(new MemoryStream(j.Data, false)))
                        if (Native.FPDFImageObj_LoadJpegFileInline(null, 0, obj, src.Access) == 0)
                            throw new PdfException(PdfErrorKind.Format, "JPEG görüntüsü okunamadı.");
                    Native.FPDFImageObj_GetImagePixelSize(obj, out var w, out var h);
                    (pxW, pxH) = ((int)w, (int)h);
                    break;
                case BitmapImageSource b:
                    (pxW, pxH) = (b.Image.Width, b.Image.Height);
                    var bgr = ToBgrOnWhite(b.Image, out int stride);
                    fixed (byte* p = bgr)
                    {
                        var bmp = Native.FPDFBitmap_CreateEx(pxW, pxH, Native.FPDFBitmap_BGR, p, stride);
                        if (bmp == 0) throw new OutOfMemoryException();
                        try
                        {
                            if (Native.FPDFImageObj_SetBitmap(null, 0, obj, bmp) == 0)
                                throw new PdfException(PdfErrorKind.Unknown, "Görüntü sayfaya eklenemedi.");
                        }
                        finally { Native.FPDFBitmap_Destroy(bmp); }
                    }
                    break;
                default: throw new ArgumentException("Bilinmeyen görüntü türü.");
            }
            if (pxW <= 0 || pxH <= 0) throw new PdfException(PdfErrorKind.Format, "Görüntü boyutu okunamadı.");

            double imgW = pxW * 72.0 / (img.DpiX > 1 ? img.DpiX : 96), imgH = pxH * 72.0 / (img.DpiY > 1 ? img.DpiY : 96);
            double pageW, pageH, drawW, drawH, x, y;
            if (layout.Paper is { } paper)
            {
                (pageW, pageH) = (paper.Width, paper.Height);
                if (layout.AutoRotate && (imgW > imgH) != (pageW > pageH)) (pageW, pageH) = (pageH, pageW);
                double availW = pageW - 2 * layout.MarginPt, availH = pageH - 2 * layout.MarginPt;
                double s = Math.Min(availW / imgW, availH / imgH);
                drawW = imgW * s; drawH = imgH * s;
                x = (pageW - drawW) / 2; y = (pageH - drawH) / 2;
            }
            else
            {
                pageW = imgW + 2 * layout.MarginPt; pageH = imgH + 2 * layout.MarginPt;
                drawW = imgW; drawH = imgH; x = y = layout.MarginPt;
            }
            ClosePages();
            int index = PageCount;
            var page = Native.FPDFPage_New(_doc, index, pageW, pageH);
            if (page == 0) throw new PdfException(PdfErrorKind.Unknown, "Sayfa oluşturulamadı.");
            try
            {
                Native.FPDFImageObj_SetMatrix(obj, drawW, 0, 0, drawH, x, y);
                Native.FPDFPage_InsertObject(page, obj);
                obj = 0; // sahiplik sayfaya geçti
                Native.FPDFPage_GenerateContent(page); // yeni boş sayfa: korunacak metin yok
            }
            finally { Native.FPDF_ClosePage(page); }
            RefreshStructure();
            MarkChanged(structure: false);
        }
        finally { if (obj != 0) Native.FPDFPageObj_Destroy(obj); }
    });

    private static byte[] ToBgrOnWhite(BgraImage img, out int stride)
    {
        stride = (img.Width * 3 + 3) & ~3;
        var dst = new byte[stride * img.Height];
        for (int y = 0; y < img.Height; y++)
        {
            int si = y * img.Stride, di = y * stride;
            for (int x = 0; x < img.Width; x++, si += 4, di += 3)
            {
                int a = img.Pixels[si + 3];
                if (a == 255) { dst[di] = img.Pixels[si]; dst[di + 1] = img.Pixels[si + 1]; dst[di + 2] = img.Pixels[si + 2]; }
                else
                {
                    int inv = 255 - a;
                    dst[di] = (byte)((img.Pixels[si] * a + 255 * inv) / 255);
                    dst[di + 1] = (byte)((img.Pixels[si + 1] * a + 255 * inv) / 255);
                    dst[di + 2] = (byte)((img.Pixels[si + 2] * a + 255 * inv) / 255);
                }
            }
        }
        return dst;
    }
}
