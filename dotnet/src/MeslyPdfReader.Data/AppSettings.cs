using System.Globalization;

namespace MeslyPdfReader.Data;

public enum ThemeChoice { System, Light, Dark }
public enum ZoomModeChoice { FitWidth, FitPage, ActualSize, Custom }
public enum LayoutChoice { Continuous, SinglePage, TwoPageContinuous, TwoPage }

/// <summary>Uygulama ayarları; SQLite'ta anahtar/değer olarak saklanır.</summary>
public sealed class AppSettings
{
    private readonly AppDatabase _db;
    public AppSettings(AppDatabase db) => _db = db;

    public event Action<string>? Changed;

    private string? Get(string k) => _db.GetSetting(k);
    private void Set(string k, string? v) { _db.SetSetting(k, v); Changed?.Invoke(k); }
    private T GetEnum<T>(string k, T def) where T : struct, Enum => Enum.TryParse<T>(Get(k), out var v) ? v : def;
    private bool GetBool(string k, bool def) => Get(k) is { } s ? s == "1" : def;
    private int GetInt(string k, int def) => int.TryParse(Get(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    private double GetDouble(string k, double def) => double.TryParse(Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    public ThemeChoice Theme { get => GetEnum("ui.theme", ThemeChoice.System); set => Set("ui.theme", value.ToString()); }
    public ZoomModeChoice DefaultZoomMode { get => GetEnum("view.zoomMode", ZoomModeChoice.FitWidth); set => Set("view.zoomMode", value.ToString()); }
    public double DefaultZoom { get => GetDouble("view.zoom", 1.0); set => Set("view.zoom", value.ToString(CultureInfo.InvariantCulture)); }
    public LayoutChoice DefaultLayout { get => GetEnum("view.layout", LayoutChoice.Continuous); set => Set("view.layout", value.ToString()); }
    /// <summary>Arayüz boyutu: 0 = otomatik (pencereye göre, afişteki oranlar), yoksa yüzde (100, 115, 130, 150).</summary>
    /// <summary>Metin tanıma motoru: 0 otomatik (hibrit), 1 yalnız gelişmiş, 2 yalnız Tesseract.</summary>
    public int OcrEngineMode { get => Math.Clamp(GetInt("ocr.engine", 0), 0, 2); set => Set("ocr.engine", value.ToString(CultureInfo.InvariantCulture)); }
    public int UiScale { get => GetInt("ui.scale", 0); set => Set("ui.scale", value.ToString(CultureInfo.InvariantCulture)); }
    public bool DarkReading { get => GetBool("view.darkReading", false); set => Set("view.darkReading", value ? "1" : "0"); }
    public bool RememberLastPage { get => GetBool("view.rememberPage", true); set => Set("view.rememberPage", value ? "1" : "0"); }
    public bool ShowThumbnails { get => GetBool("view.thumbnails", true); set => Set("view.thumbnails", value ? "1" : "0"); }
    public int RecentLimit { get => Math.Clamp(GetInt("files.recentLimit", 20), 0, 100); set => Set("files.recentLimit", value.ToString(CultureInfo.InvariantCulture)); }
    public bool KeepHistory { get => GetBool("files.keepHistory", true); set => Set("files.keepHistory", value ? "1" : "0"); }
    public bool AutoRecovery { get => GetBool("files.autoRecovery", true); set => Set("files.autoRecovery", value ? "1" : "0"); }
    public int AutoRecoveryMinutes { get => Math.Clamp(GetInt("files.autoRecoveryMinutes", 3), 1, 60); set => Set("files.autoRecoveryMinutes", value.ToString(CultureInfo.InvariantCulture)); }
    public int TempRetentionDays { get => Math.Clamp(GetInt("files.tempDays", 7), 1, 365); set => Set("files.tempDays", value.ToString(CultureInfo.InvariantCulture)); }
    public string? DefaultOpenFolder { get => Get("files.openFolder"); set => Set("files.openFolder", value); }
    public string? DefaultSaveFolder { get => Get("files.saveFolder"); set => Set("files.saveFolder", value); }
    public int RenderCacheMb { get => Math.Clamp(GetInt("perf.cacheMb", 384), 64, 4096); set => Set("perf.cacheMb", value.ToString(CultureInfo.InvariantCulture)); }
    public bool HardwareAcceleration { get => GetBool("perf.hardware", true); set => Set("perf.hardware", value ? "1" : "0"); }
    public int ExportDpi { get => Math.Clamp(GetInt("export.dpi", 200), 36, 1200); set => Set("export.dpi", value.ToString(CultureInfo.InvariantCulture)); }
    public int JpegQuality { get => Math.Clamp(GetInt("export.jpegQuality", 90), 10, 100); set => Set("export.jpegQuality", value.ToString(CultureInfo.InvariantCulture)); }
    public string? ScanDevice { get => Get("scan.device"); set => Set("scan.device", value); }
    public int ScanSource { get => GetInt("scan.source", 0); set => Set("scan.source", value.ToString(CultureInfo.InvariantCulture)); }
    public int ScanColor { get => GetInt("scan.color", 0); set => Set("scan.color", value.ToString(CultureInfo.InvariantCulture)); }
    public int ScanDpi { get => Math.Clamp(GetInt("scan.dpi", 300), 75, 1200); set => Set("scan.dpi", value.ToString(CultureInfo.InvariantCulture)); }
    public int ScanPaper { get => GetInt("scan.paper", 0); set => Set("scan.paper", value.ToString(CultureInfo.InvariantCulture)); }
    public bool ScanAutoCorrect { get => GetBool("scan.autoCorrect", true); set => Set("scan.autoCorrect", value ? "1" : "0"); }
    public bool ScanOcr { get => GetBool("scan.ocr", true); set => Set("scan.ocr", value ? "1" : "0"); }
    public string OcrLanguages { get => Get("ocr.languages") is { Length: > 0 } v ? v : "tur+eng"; set => Set("ocr.languages", value); }
    /// <summary>Taranmış (metinsiz) sayfaları belge açılınca arka planda otomatik tanı ve düzelt.</summary>
    public bool OcrAutoOnOpen { get => GetBool("ocr.autoOnOpen", true); set => Set("ocr.autoOnOpen", value ? "1" : "0"); }
    public bool OcrAutoRotate { get => GetBool("ocr.autoRotate", true); set => Set("ocr.autoRotate", value ? "1" : "0"); }
    public bool OcrDeskew { get => GetBool("ocr.deskew", true); set => Set("ocr.deskew", value ? "1" : "0"); }
    public bool OcrCleanBorders { get => GetBool("ocr.cleanBorders", true); set => Set("ocr.cleanBorders", value ? "1" : "0"); }
    public bool OcrWhiten { get => GetBool("ocr.whiten", true); set => Set("ocr.whiten", value ? "1" : "0"); }
    public int OcrDpi { get => Math.Clamp(GetInt("ocr.dpi", 300), 150, 600); set => Set("ocr.dpi", value.ToString(CultureInfo.InvariantCulture)); }
    public bool ConvertOcr { get => GetBool("convert.ocr", true); set => Set("convert.ocr", value ? "1" : "0"); }
    public bool ConvertTables { get => GetBool("convert.tables", true); set => Set("convert.tables", value ? "1" : "0"); }
    public bool ConvertImages { get => GetBool("convert.images", true); set => Set("convert.images", value ? "1" : "0"); }
    public bool ConvertSheetPerPage { get => GetBool("convert.sheetPerPage", true); set => Set("convert.sheetPerPage", value ? "1" : "0"); }
    public bool ConvertTablesOnly { get => GetBool("convert.tablesOnly", false); set => Set("convert.tablesOnly", value ? "1" : "0"); }
    public bool ConvertOpenAfter { get => GetBool("convert.openAfter", true); set => Set("convert.openAfter", value ? "1" : "0"); }
    /// <summary>0 = yüksek kalite, 1 = dengeli, 2 = en küçük dosya.</summary>
    public int CompressLevel { get => Math.Clamp(GetInt("compress.level", 1), 0, 2); set => Set("compress.level", value.ToString(CultureInfo.InvariantCulture)); }
    public bool CompressGray { get => GetBool("compress.gray", false); set => Set("compress.gray", value ? "1" : "0"); }
    public bool ConfirmExternalLinks { get => GetBool("security.confirmLinks", true); set => Set("security.confirmLinks", value ? "1" : "0"); }

    // ── Afiş düzeni (0.7) ──
    /// <summary>Açılışta Ana Sayfa gösterilsin; kapalıysa önceki oturumda açık olan belgeler yeniden açılır.</summary>
    public bool StartupHome { get => GetBool("general.startupHome", true); set => Set("general.startupHome", value ? "1" : "0"); }
    /// <summary>Son oturumda açık belgeler ('|' ile ayrılmış).</summary>
    public string? LastSession { get => Get("general.lastSession"); set => Set("general.lastSession", value); }
    /// <summary>Yeni sürüm denetimi (yalnız sürüm numarası sorulur).</summary>
    public bool UpdateCheck { get => GetBool("update.check", true); set => Set("update.check", value ? "1" : "0"); }
    public string? LastUpdateCheck { get => Get("update.last"); set => Set("update.last", value); }
    public bool OcrDenoise { get => GetBool("ocr.denoise", true); set => Set("ocr.denoise", value ? "1" : "0"); }
    public bool OcrKeepLayout { get => GetBool("ocr.keepLayout", true); set => Set("ocr.keepLayout", value ? "1" : "0"); }
    /// <summary>OCR ekranı çıktı türü: 0 düzenlenebilir metin, 1 aranabilir PDF, 2 Word, 3 düz metin.</summary>
    public int OcrOutput { get => Math.Clamp(GetInt("ocr.output", 0), 0, 3); set => Set("ocr.output", value.ToString(CultureInfo.InvariantCulture)); }
    public bool ScanAutoCrop { get => GetBool("scan.autoCrop", true); set => Set("scan.autoCrop", value ? "1" : "0"); }
    public bool ScanFeeder { get => GetBool("scan.feeder", false); set => Set("scan.feeder", value ? "1" : "0"); }
    /// <summary>Tarama dosya türü: 0 PDF, 1 JPG, 2 PNG.</summary>
    public int ScanFileType { get => Math.Clamp(GetInt("scan.fileType", 0), 0, 2); set => Set("scan.fileType", value.ToString(CultureInfo.InvariantCulture)); }
    /// <summary>Tarama hedefi: 0 yeni PDF, 1 mevcut PDF'e ekle, 2 resim olarak kaydet.</summary>
    public int ScanTarget { get => Math.Clamp(GetInt("scan.target", 0), 0, 2); set => Set("scan.target", value.ToString(CultureInfo.InvariantCulture)); }
    /// <summary>Toplu dönüştürme çıktı klasörü; boşsa her dosyanın kendi klasörü.</summary>
    /// <summary>PDF → Resim: 0 PNG, 1 JPG.</summary>
    public int ExportImageFormat { get => Math.Clamp(GetInt("export.imageFormat", 0), 0, 1); set => Set("export.imageFormat", value.ToString(CultureInfo.InvariantCulture)); }
    public string? BatchOutputFolder { get => Get("batch.output"); set => Set("batch.output", value); }
    /// <summary>İmza sekmesindeki “Ad Soyad” için hatırlanan ad.</summary>
    public string? SignName { get => Get("sign.name"); set => Set("sign.name", value); }
    public string AnnotColor { get => Get("annot.color") is { Length: 7 } v ? v : "#FFE14D"; set => Set("annot.color", value); }

    /// <summary>Tüm ayarları varsayılana döndürür (belgeler ve geçmiş etkilenmez).</summary>
    public void ResetAll()
    {
        foreach (var k in _db.GetAllSettings().Keys.ToList()) _db.SetSetting(k, null);
        Changed?.Invoke("*");
    }
}
