using System.Diagnostics;
using Microsoft.Win32;

namespace MeslyPdfReader.App.Services;

/// <summary>
/// “Varsayılan PDF görüntüleyici”. Windows 10/11, bir programın kendini varsayılan yapmasına izin vermez
/// (UserChoice karması korumalıdır); bu yüzden durum okunur ve Windows'un Varsayılan uygulamalar sayfası açılır —
/// Windows 11'de doğrudan Mesly PDF Reader'ın sayfası.
/// </summary>
public static class DefaultAppService
{
    public const string ProgId = "MeslyPdfReader.Document";
    public const string RegisteredName = "Mesly PDF Reader";

    /// <summary>.pdf dosyaları şu an Mesly ile mi açılıyor?</summary>
    public static bool IsDefault()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice");
            if (k?.GetValue("ProgId") is string id) return id.Equals(ProgId, StringComparison.OrdinalIgnoreCase);
            using var c = Registry.ClassesRoot.OpenSubKey(".pdf");
            return c?.GetValue(null) is string def && def.Equals(ProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Kurulum, uygulamayı “Varsayılan uygulamalar” listesine kaydetmiş mi?</summary>
    public static bool IsRegistered(out bool perUser)
    {
        perUser = false;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using (var u = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications"))
                if (u?.GetValue(RegisteredName) is not null) { perUser = true; return true; }
            using var m = Registry.LocalMachine.OpenSubKey(@"Software\RegisteredApplications");
            return m?.GetValue(RegisteredName) is not null;
        }
        catch { return false; }
    }

    /// <summary>Windows'un Varsayılan uygulamalar ayarını açar (Windows 11'de Mesly'nin kendi sayfası).</summary>
    public static void OpenSettings()
    {
        string uri = "ms-settings:defaultapps";
        if (IsRegistered(out bool perUser) && Environment.OSVersion.Version.Build >= 22000)
            uri += (perUser ? "?registeredAppUser=" : "?registeredAppMachine=") + Uri.EscapeDataString(RegisteredName);
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch { } }
    }
}
