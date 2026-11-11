using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.Services;

/// <summary>WPF kodlayıcıları ile görüntü okuma/yazma ve BGRA dönüşümleri.</summary>
public static class ImageCodec
{
    public static BitmapSource ToBitmapSource(BgraImage img, double dpi = 96)
    {
        var bs = BitmapSource.Create(img.Width, img.Height, dpi, dpi, PixelFormats.Bgra32, null, img.Pixels, img.Stride);
        bs.Freeze();
        return bs;
    }

    public static BgraImage FromBitmapSource(BitmapSource src)
    {
        var conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int stride = conv.PixelWidth * 4;
        var px = new byte[stride * conv.PixelHeight];
        conv.CopyPixels(px, stride, 0);
        return new BgraImage { Width = conv.PixelWidth, Height = conv.PixelHeight, Stride = stride, Pixels = px };
    }

    /// <summary>Görüntü dosyasını PDF sayfa kaynağına çevirir. JPEG'ler yeniden sıkıştırılmaz. TIFF'te tüm kareler alınır.</summary>
    public static IReadOnlyList<ImagePageSource> LoadForPdf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        using var fs = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        var list = new List<ImagePageSource>();
        if ((ext is ".jpg" or ".jpeg") && decoder.Frames.Count == 1 && IsPlainJpeg(decoder.Frames[0]))
        {
            var f = decoder.Frames[0];
            list.Add(new JpegImageSource(File.ReadAllBytes(path), Dpi(f.DpiX), Dpi(f.DpiY)));
            return list;
        }
        foreach (var frame in decoder.Frames)
        {
            BitmapSource bs = frame;
            var orientation = ReadOrientation(frame);
            if (orientation != 0) bs = new TransformedBitmap(bs, new RotateTransform(orientation));
            list.Add(new BitmapImageSource(FromBitmapSource(bs), Dpi(frame.DpiX), Dpi(frame.DpiY)));
        }
        return list;
    }

    private static double Dpi(double d) => d is > 10 and < 5000 ? d : 96;

    private static bool IsPlainJpeg(BitmapFrame f)
    {
        // EXIF yönü olan veya CMYK JPEG'ler bitmap olarak işlenir (doğru görünüm için).
        if (ReadOrientation(f) != 0) return false;
        return f.Format == PixelFormats.Bgr24 || f.Format == PixelFormats.Gray8 || f.Format == PixelFormats.Bgr32 || f.Format == PixelFormats.Rgb24;
    }

    private static int ReadOrientation(BitmapFrame f)
    {
        try
        {
            if (f.Metadata is BitmapMetadata md && md.ContainsQuery("System.Photo.Orientation"))
                return System.Convert.ToInt32(md.GetQuery("System.Photo.Orientation")) switch { 6 => 90, 3 => 180, 8 => 270, _ => 0 };
        }
        catch { }
        return 0;
    }

    /// <summary>Bayt dizisindeki görüntüyü (JPEG/PNG/TIFF/BMP) BGRA'ya çözer.</summary>
    public static BgraImage Decode(byte[] data)
    {
        using var ms = new MemoryStream(data, false);
        var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        return FromBitmapSource(frame);
    }

    public static byte[] EncodeJpeg(BgraImage img, int quality = 90, double dpi = 300, bool gray = false)
    {
        var enc = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 10, 100) };
        // JPEG saydamlık taşımaz: BGR24'e (veya gri) çevir.
        var bgr = new FormatConvertedBitmap(BitmapSource.Create(img.Width, img.Height, dpi, dpi, PixelFormats.Bgra32, null, img.Pixels, img.Stride), gray ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, 0);
        enc.Frames.Add(BitmapFrame.Create(bgr));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static byte[] EncodePng(BgraImage img, double dpi = 300)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(img.Width, img.Height, dpi, dpi, PixelFormats.Bgra32, null, img.Pixels, img.Stride)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static void Save(BgraImage img, string path, ImageFormatKind kind, double dpi, int jpegQuality = 90)
    {
        if (kind == ImageFormatKind.Png)
        {
            using var fs = File.Create(path);
            PngEncoder.Write(img, fs, dpi);
            return;
        }
        BitmapEncoder enc = kind switch
        {
            ImageFormatKind.Jpeg => new JpegBitmapEncoder { QualityLevel = jpegQuality },
            ImageFormatKind.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            _ => new BmpBitmapEncoder(),
        };
        enc.Frames.Add(BitmapFrame.Create(Opaque(img, dpi)));
        using var f = File.Create(path);
        enc.Save(f);
    }

    /// <summary>Çok sayfalı TIFF yazar.</summary>
    public static void SaveMultiPageTiff(IEnumerable<BgraImage> pages, string path, double dpi)
    {
        var enc = new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw };
        foreach (var p in pages) enc.Frames.Add(BitmapFrame.Create(Opaque(p, dpi)));
        using var f = File.Create(path);
        enc.Save(f);
    }

    private static BitmapSource Opaque(BgraImage img, double dpi)
    {
        var bs = BitmapSource.Create(img.Width, img.Height, dpi, dpi, PixelFormats.Bgra32, null, img.Pixels, img.Stride);
        var conv = new FormatConvertedBitmap(bs, PixelFormats.Bgr24, null, 0);
        conv.Freeze();
        return conv;
    }

    /// <summary>
    /// Karanlık okuma: parlaklığı ters çevirir, renk tonunu korur (fotoğraflar negatif görünmez gibi değil
    /// ama renkler kaybolmaz). Beyaz arka plan antrasite, siyah metin açık griye döner.
    /// </summary>
    public static void ApplyDarkReading(BgraImage img)
    {
        var p = img.Pixels;
        const int lo = 28, hi = 222;
        for (int y = 0; y < img.Height; y++)
        {
            int i = y * img.Stride, end = i + img.Width * 4;
            for (; i < end; i += 4)
            {
                int b = p[i], g = p[i + 1], r = p[i + 2];
                int lum = (r * 299 + g * 587 + b * 114) / 1000;
                int nl = lo + (255 - lum) * (hi - lo) / 255;
                int d = nl - lum;
                p[i] = Clamp(b + d); p[i + 1] = Clamp(g + d); p[i + 2] = Clamp(r + d);
            }
        }
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    public static BitmapImage LoadAppImage(string packPath)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.UriSource = new Uri($"pack://application:,,,/{packPath}");
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    /// <summary>Bir WPF görselinin (pencere vb.) ekran görüntüsünü PNG olarak kaydeder.</summary>
    public static void CaptureVisual(FrameworkElement element, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        int w = (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), h = (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = (element as System.Windows.Controls.Control)?.Background ?? Brushes.White;
            dc.DrawRectangle(bg is SolidColorBrush { Color.A: 255 } ? bg : Brushes.White, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
