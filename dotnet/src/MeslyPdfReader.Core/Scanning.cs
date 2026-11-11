namespace MeslyPdfReader.Core;

public enum ScanSource { Flatbed, Feeder, FeederDuplex }
public enum ScanColorMode { Color, Gray, BlackWhite }

public sealed record ScannerInfo(string Id, string Name, string? Manufacturer)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Manufacturer) || Name.Contains(Manufacturer, StringComparison.OrdinalIgnoreCase)
        ? Name : $"{Name} ({Manufacturer})";
}

public sealed record ScannerCapabilities(bool HasFlatbed, bool HasFeeder, bool HasDuplex, bool FeederLoaded, int MaxDpi);

/// <param name="Paper">null: tarayıcının tüm alanı.</param>
public sealed record ScanSettings(ScanSource Source, ScanColorMode Color, int Dpi, PaperSize? Paper, int Brightness = 0, int Contrast = 0);

public enum ScannedFormat { Jpeg, Png, Bmp, Tiff }

/// <summary>Taranmış tek bir sayfa (tarayıcının verdiği ham görüntü dosyası).</summary>
public sealed class ScannedPage
{
    public required byte[] Data { get; init; }
    public required ScannedFormat Format { get; init; }
    public required int Dpi { get; init; }
    /// <summary>Kullanıcının uyguladığı döndürme (90° adım).</summary>
    public int QuarterTurns { get; set; }
}

public enum ScanErrorKind { NoDevice, ServiceUnavailable, Offline, Busy, WarmingUp, PaperEmpty, PaperJam, PaperProblem, CoverOpen, Cancelled, Unsupported, Other }

public sealed class ScanException : Exception
{
    public ScanErrorKind Kind { get; }
    public ScanException(ScanErrorKind kind, string? detail = null, Exception? inner = null) : base(Describe(kind, detail), inner) => Kind = kind;

    public static string Describe(ScanErrorKind kind, string? detail = null)
    {
        var m = kind switch
        {
            ScanErrorKind.NoDevice => "Bağlı tarayıcı bulunamadı. Tarayıcının açık ve bilgisayara bağlı olduğundan, sürücüsünün kurulu olduğundan emin olun.",
            ScanErrorKind.ServiceUnavailable => "Windows Görüntü Alma (WIA) hizmetine ulaşılamadı. Hizmetler'de “Windows Görüntü Alma (WIA)” hizmetinin çalıştığını denetleyin.",
            ScanErrorKind.Offline => "Tarayıcı çevrimdışı. Bağlantısını ve gücünü denetleyin.",
            ScanErrorKind.Busy => "Tarayıcı başka bir işlem tarafından kullanılıyor. Diğer tarama programlarını kapatıp tekrar deneyin.",
            ScanErrorKind.WarmingUp => "Tarayıcı ısınıyor. Birkaç saniye sonra tekrar deneyin.",
            ScanErrorKind.PaperEmpty => "Belge besleyicide kâğıt yok. Sayfaları besleyiciye yerleştirip tekrar deneyin.",
            ScanErrorKind.PaperJam => "Belge besleyicide kâğıt sıkıştı. Sıkışan kâğıdı çıkarıp kalan sayfaları yeniden tarayın.",
            ScanErrorKind.PaperProblem => "Kâğıt beslemede sorun oluştu.",
            ScanErrorKind.CoverOpen => "Tarayıcı kapağı açık.",
            ScanErrorKind.Cancelled => "Tarama iptal edildi.",
            ScanErrorKind.Unsupported => "Tarayıcı istenen ayarı desteklemiyor.",
            _ => "Tarama sırasında bir hata oluştu.",
        };
        return string.IsNullOrWhiteSpace(detail) ? m : $"{m} ({detail})";
    }
}

/// <summary>Tarayıcı sürücüsüyle konuşan alt katman (Windows'ta WIA). Tüm çağrılar aynı iş parçacığından yapılır.</summary>
public interface IScannerBackend
{
    /// <summary>Cihaza bağlanır ve ayarları uygular.</summary>
    void Begin(string deviceId, ScanSettings settings);
    /// <summary>Sonraki sayfayı alır. Besleyicide kâğıt bitince <see cref="ScanErrorKind.PaperEmpty"/> fırlatır.</summary>
    ScannedPage AcquireNext();
    void End();
}

public sealed record ScanResult(IReadOnlyList<ScannedPage> Pages, ScanException? StoppedBy)
{
    /// <summary>Hata olduysa bile o ana kadar taranan sayfalar korunur.</summary>
    public bool Partial => StoppedBy is not null && Pages.Count > 0;
}

/// <summary>Tarama işi: düz camdan tek sayfa, belge besleyiciden kâğıt bitene kadar tüm sayfalar.</summary>
public static class ScanJob
{
    public const int MaxFeederPages = 999;

    public static ScanResult Run(IScannerBackend backend, string deviceId, ScanSettings settings, Action<ScannedPage>? onPage = null, CancellationToken ct = default)
    {
        var pages = new List<ScannedPage>();
        backend.Begin(deviceId, settings);
        try
        {
            if (settings.Source == ScanSource.Flatbed)
            {
                ct.ThrowIfCancellationRequested();
                var p = backend.AcquireNext();
                pages.Add(p);
                onPage?.Invoke(p);
                return new ScanResult(pages, null);
            }
            while (pages.Count < MaxFeederPages)
            {
                if (ct.IsCancellationRequested) return new ScanResult(pages, new ScanException(ScanErrorKind.Cancelled));
                ScannedPage p;
                try { p = backend.AcquireNext(); }
                catch (ScanException ex) when (ex.Kind == ScanErrorKind.PaperEmpty && pages.Count > 0)
                {
                    break; // besleyici boşaldı: normal bitiş
                }
                catch (ScanException ex) when (pages.Count > 0)
                {
                    return new ScanResult(pages, ex); // sıkışma vb.: taranan sayfalar korunur
                }
                pages.Add(p);
                onPage?.Invoke(p);
            }
            return new ScanResult(pages, null);
        }
        finally { backend.End(); }
    }

    /// <summary>Kâğıt boyutunu tarayıcı pikseline çevirir; tarayıcının en büyük alanını aşmaz.</summary>
    public static (int Width, int Height) ExtentPixels(PaperSize? paper, int dpi, int maxWidthPx, int maxHeightPx)
    {
        if (paper is null) return (maxWidthPx, maxHeightPx);
        int w = (int)Math.Round(paper.WidthMm / 25.4 * dpi), h = (int)Math.Round(paper.HeightMm / 25.4 * dpi);
        if (maxWidthPx > 0) w = Math.Min(w, maxWidthPx);
        if (maxHeightPx > 0) h = Math.Min(h, maxHeightPx);
        return (Math.Max(1, w), Math.Max(1, h));
    }
}
