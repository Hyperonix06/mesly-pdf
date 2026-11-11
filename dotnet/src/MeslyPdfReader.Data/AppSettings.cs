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
    public bool ConfirmExternalLinks { get => GetBool("security.confirmLinks", true); set => Set("security.confirmLinks", value ? "1" : "0"); }
}
