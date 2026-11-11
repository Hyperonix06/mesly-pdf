using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MeslyPdfReader.Core;

namespace MeslyPdfReader.Ocr;

public sealed class OcrException(string message) : Exception(message);

/// <summary>
/// Tesseract 5 OCR motoru. Bir örnek aynı anda tek iş parçacığından kullanılır; <see cref="OcrService"/> örnek havuzu tutar.
/// Belgeler hiçbir zaman ağa gönderilmez: tanıma tamamen bu bilgisayarda yapılır.
/// </summary>
public sealed unsafe class OcrEngine : IDisposable
{
    private nint _api;
    public string Languages { get; }

    private OcrEngine(nint api, string languages) { _api = api; Languages = languages; }

    /// <summary>Dil verisi klasörü: uygulama klasöründeki tessdata, MESLY_TESSDATA ortam değişkeni veya sistem tessdata.</summary>
    public static string? FindTessdata()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("MESLY_TESSDATA"),
            Path.Combine(AppContext.BaseDirectory, "ocr", "tessdata"),
            Path.Combine(AppContext.BaseDirectory, "tessdata"),
            "/usr/share/tesseract-ocr/5/tessdata", "/usr/share/tesseract-ocr/4.00/tessdata", "/usr/share/tessdata",
        };
        return candidates.FirstOrDefault(d => !string.IsNullOrEmpty(d) && File.Exists(Path.Combine(d!, "eng.traineddata")));
    }

    public static IReadOnlyList<string> InstalledLanguages()
    {
        var d = FindTessdata();
        if (d is null) return Array.Empty<string>();
        return Directory.GetFiles(d, "*.traineddata").Select(f => Path.GetFileNameWithoutExtension(f)!).Where(n => n != "osd").OrderBy(n => n).ToList();
    }

    /// <summary>Motor kullanılabilir mi; değilse kullanıcıya gösterilecek Türkçe neden.</summary>
    public static string? Unavailable()
    {
        var err = Tess.EnsureLoaded();
        if (err is not null) return err;
        if (FindTessdata() is null) return "OCR dil verileri (tessdata) bulunamadı. Uygulamayı yeniden kurmayı deneyin.";
        return null;
    }

    public static string Version()
    {
        if (Tess.EnsureLoaded() is { } e) return e;
        return Tess.Utf8(Tess.TessVersion()) ?? "?";
    }

    public static OcrEngine Create(string languages)
    {
        if (Unavailable() is { } why) throw new OcrException(why);
        var data = FindTessdata()!;
        var missing = languages.Split('+').Where(l => !File.Exists(Path.Combine(data, l + ".traineddata"))).ToList();
        if (missing.Count > 0) throw new OcrException($"OCR dili kurulu değil: {string.Join(", ", missing.Select(OcrLanguage.NameOf))}.");
        var api = Tess.TessBaseAPICreate();
        if (Tess.TessBaseAPIInit3(api, data, languages) != 0)
        {
            Tess.TessBaseAPIDelete(api);
            throw new OcrException($"OCR motoru “{languages}” diliyle başlatılamadı.");
        }
        // Gereksiz hata ayıklama dosyaları yazmasın; sözlük düzeltmesi açık kalsın.
        Tess.TessBaseAPISetVariable(api, "debug_file", OperatingSystem.IsWindows() ? "NUL" : "/dev/null");
        Tess.TessBaseAPISetVariable(api, "preserve_interword_spaces", "0");
        return new OcrEngine(api, languages);
    }

    // ─────────────── Tanıma ───────────────

    private static readonly ConcurrentDictionary<nint, CancellationToken> Cancels = new();
    private static long _cancelSeq;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int CancelCallback(nint cancelThis, int words) =>
        Cancels.TryGetValue(cancelThis, out var ct) && ct.IsCancellationRequested ? 1 : 0;

    /// <summary>Gri görüntüyü tanır. <paramref name="progress"/> 0–1.</summary>
    public OcrPage Recognize(GrayImage img, int dpi, CancellationToken ct = default, Action<double>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_api == 0, this);
        ct.ThrowIfCancellationRequested();
        Tess.TessBaseAPISetPageSegMode(_api, Tess.PSM_AUTO);
        fixed (byte* p = img.Data) Tess.TessBaseAPISetImage(_api, p, img.Width, img.Height, 1, img.Width);
        Tess.TessBaseAPISetSourceResolution(_api, Math.Clamp(dpi, 70, 1200));

        var monitor = Tess.TessMonitorCreate();
        var key = (nint)Interlocked.Increment(ref _cancelSeq);
        Cancels[key] = ct;
        Tess.TessMonitorSetCancelFunc(monitor, &CancelCallback);
        Tess.TessMonitorSetCancelThis(monitor, key);
        using var timer = progress is null ? null : new Timer(_ => progress(Tess.TessMonitorGetProgress(monitor) / 100.0), null, 200, 200);
        int rc;
        try { rc = Tess.TessBaseAPIRecognize(_api, monitor); }
        finally
        {
            timer?.Change(Timeout.Infinite, Timeout.Infinite);
            Cancels.TryRemove(key, out _);
            Tess.TessMonitorDelete(monitor);
        }
        ct.ThrowIfCancellationRequested();
        if (rc != 0) throw new OcrException("Metin tanıma başarısız oldu.");

        var words = new List<OcrWord>();
        var it = Tess.TessBaseAPIGetIterator(_api);
        if (it != 0)
        {
            try
            {
                var pit = Tess.TessResultIteratorGetPageIteratorConst(it);
                int line = -1, block = -1, baseY = 0;
                do
                {
                    if (Tess.TessPageIteratorIsAtBeginningOf(pit, Tess.RIL_BLOCK) != 0) block++;
                    if (Tess.TessPageIteratorIsAtBeginningOf(pit, Tess.RIL_TEXTLINE) != 0)
                    {
                        line++;
                        baseY = Tess.TessPageIteratorBaseline(pit, Tess.RIL_TEXTLINE, out _, out var y1, out _, out var y2) != 0 ? (y1 + y2) / 2 : -1;
                    }
                    var tp = Tess.TessResultIteratorGetUTF8Text(it, Tess.RIL_WORD);
                    var text = Tess.Utf8(tp);
                    if (tp != null) Tess.TessDeleteText(tp);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (Tess.TessPageIteratorBoundingBox(pit, Tess.RIL_WORD, out var l, out var t, out var r, out var b) == 0) continue;
                    float conf = Tess.TessResultIteratorConfidence(it, Tess.RIL_WORD);
                    words.Add(new OcrWord(Normalize(text.Trim()), l, t, r, b, conf, Math.Max(0, line), Math.Max(0, block), baseY > 0 ? baseY : b));
                } while (Tess.TessResultIteratorNext(it, Tess.RIL_WORD) != 0);
            }
            finally { Tess.TessResultIteratorDelete(it); }
        }
        Tess.TessBaseAPIClear(_api);
        return new OcrPage(img.Width, img.Height, dpi, words, Languages);
    }

    /// <summary>Tanınan metindeki tipografik/karışık karakterleri düzeltir (ör. ﬁ bağlacı, yanlış kesme işareti).</summary>
    internal static string Normalize(string s) => s
        .Replace("ﬁ", "fi").Replace("ﬂ", "fl").Replace("ﬀ", "ff").Replace("ﬃ", "ffi").Replace("ﬄ", "ffl")
        .Replace('’', '\'').Replace('‘', '\'').Replace("­", "");

    /// <summary>
    /// Sayfa yönünü algılar (OSD). Döndürülmesi gereken saat yönü açı (0/90/180/270) ve güven döner; metin yetersizse null.
    /// </summary>
    public static (int RotateClockwise, float Confidence)? DetectOrientation(GrayImage img, int dpi)
    {
        if (Unavailable() is not null) return null;
        var data = FindTessdata()!;
        if (!File.Exists(Path.Combine(data, "osd.traineddata"))) return null;
        var api = Tess.TessBaseAPICreate();
        try
        {
            if (Tess.TessBaseAPIInit3(api, data, "osd") != 0) return null;
            Tess.TessBaseAPISetVariable(api, "debug_file", OperatingSystem.IsWindows() ? "NUL" : "/dev/null");
            Tess.TessBaseAPISetPageSegMode(api, Tess.PSM_OSD_ONLY);
            fixed (byte* p = img.Data) Tess.TessBaseAPISetImage(api, p, img.Width, img.Height, 1, img.Width);
            Tess.TessBaseAPISetSourceResolution(api, Math.Clamp(dpi, 70, 1200));
            if (Tess.TessBaseAPIDetectOrientationScript(api, out int deg, out float conf, out _, out _) == 0) return null;
            // Tesseract “görüntü saat yönünde deg kadar dönmüş” der; düzeltmek için saat yönünde (360 - deg) döndürülür.
            return ((360 - deg) % 360, conf);
        }
        finally { Tess.TessBaseAPIEnd(api); Tess.TessBaseAPIDelete(api); }
    }

    public void Dispose()
    {
        if (_api == 0) return;
        Tess.TessBaseAPIEnd(_api);
        Tess.TessBaseAPIDelete(_api);
        _api = 0;
    }
}
