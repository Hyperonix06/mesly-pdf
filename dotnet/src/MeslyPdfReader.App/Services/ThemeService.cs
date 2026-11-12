using System.Windows;
using System.Windows.Media;
using MeslyPdfReader.Data;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MeslyPdfReader.App.Services;

/// <summary>Açık / koyu / sistem teması ve Mesly renk paleti (antrasit, beyaz, yumuşak mavi).</summary>
public sealed class ThemeService
{
    private readonly AppSettings _settings;
    public bool IsDark { get; private set; }
    public event Action? ThemeChanged;

    public ThemeService(AppSettings settings) => _settings = settings;

    public void ApplyFromSettings() => Apply(_settings.Theme);

    public void Apply(ThemeChoice choice)
    {
        bool dark = choice switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => ApplicationThemeManager.GetSystemTheme() is SystemTheme.Dark or SystemTheme.CapturedMotion or SystemTheme.Glow,
        };
        IsDark = dark;
        var theme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        try { ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, updateAccent: false); }
        catch (Exception ex) { Log.Warn("Tema uygulanamadı: " + ex.Message); }

        var accent = dark ? Color.FromRgb(0xF0, 0x4A, 0x4C) : Color.FromRgb(0xD9, 0x13, 0x15);
        try { ApplicationAccentColorManager.Apply(accent, theme, false, false); } catch { }

        var r = Application.Current.Resources;
        Set(r, "MeslyAccentBrush", accent);
        Set(r, "MeslyAccentSoftBrush", dark ? Color.FromRgb(0x3A, 0x1F, 0x22) : Color.FromRgb(0xFD, 0xE7, 0xE6));
        Set(r, "MeslyAccentDarkBrush", dark ? Color.FromRgb(0xD9, 0x30, 0x33) : Color.FromRgb(0xB8, 0x0F, 0x12));
        Set(r, "MeslyNavSelectedBrush", dark ? Color.FromRgb(0xC4, 0x24, 0x27) : Color.FromRgb(0xC4, 0x19, 0x1A));
        Set(r, "MeslyCategorySelectedBrush", dark ? Color.FromRgb(0x4A, 0x24, 0x27) : Color.FromRgb(0xFC, 0xD3, 0xD1));
        Set(r, "MeslySubtleBrush", dark ? Color.FromRgb(0x2A, 0x2E, 0x35) : Color.FromRgb(0xF3, 0xF4, 0xF6));
        Set(r, "MeslySelectedBrush", dark ? Color.FromRgb(0x3A, 0x1F, 0x22) : Color.FromRgb(0xFD, 0xE7, 0xE6));
        Set(r, "MeslyCanvasBrush", dark ? Color.FromRgb(0x16, 0x19, 0x1E) : Color.FromRgb(0xF1, 0xF1, 0xF3));
        Set(r, "MeslyPanelBrush", dark ? Color.FromRgb(0x23, 0x27, 0x2E) : Colors.White);
        Set(r, "MeslyChromeBrush", dark ? Color.FromRgb(0x1C, 0x1F, 0x25) : Color.FromRgb(0xF6, 0xF7, 0xF9));
        Set(r, "MeslyBorderBrush", dark ? Color.FromRgb(0x33, 0x38, 0x41) : Color.FromRgb(0xE1, 0xE4, 0xEA));
        Set(r, "MeslyHoverBrush", dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0, 0, 0));
        Set(r, "MeslyMutedTextBrush", dark ? Color.FromRgb(0x9A, 0xA3, 0xAF) : Color.FromRgb(0x6B, 0x72, 0x80));
        Set(r, "MeslyDropZoneBrush", dark ? Color.FromRgb(0x2A, 0x1E, 0x20) : Color.FromRgb(0xFF, 0xF5, 0xF5));
        ThemeChanged?.Invoke();
    }

    private static void Set(ResourceDictionary r, string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        r[key] = b;
    }
}
