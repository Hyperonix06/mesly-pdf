using System.IO;
using System.Windows.Media.Imaging;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.Services;

/// <summary>Taranan sayfalardan PDF oluşturur. JPEG taramalar yeniden sıkıştırılmadan gömülür; sayfa boyutu tarama alanına eşittir.</summary>
public static class ScanPdfBuilder
{
    public static async Task<PdfDocument> BuildAsync(IReadOnlyList<ScannedPage> pages, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var sources = await Task.Run(() => pages.Select(ToSource).ToList(), ct);
        var doc = await PdfDocument.CreateFromImagesAsync(sources, new ImagePageLayout(), progress, ct);
        try
        {
            for (int i = 0; i < pages.Count; i++)
                if (pages[i].QuarterTurns % 4 != 0) await doc.RotatePagesAsync(new[] { i }, pages[i].QuarterTurns);
            return doc;
        }
        catch { doc.Dispose(); throw; }
    }

    private static ImagePageSource ToSource(ScannedPage p)
    {
        if (p.Format == ScannedFormat.Jpeg) return new JpegImageSource(p.Data, p.Dpi, p.Dpi);
        using var ms = new MemoryStream(p.Data);
        var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        double dpi = frame.DpiX is > 10 and < 5000 ? frame.DpiX : p.Dpi;
        return new BitmapImageSource(ImageCodec.FromBitmapSource(frame), dpi, dpi);
    }
}
