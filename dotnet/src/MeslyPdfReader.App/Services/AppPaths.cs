using System.IO;

namespace MeslyPdfReader.App.Services;

/// <summary>Kullanıcıya özel uygulama klasörleri (%LOCALAPPDATA%\Mesly\PdfReader).</summary>
public static class AppPaths
{
    public static string Root { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mesly", "PdfReader");
    public static string Database => Path.Combine(Root, "mesly.db");
    public static string Temp => Path.Combine(Root, "temp");
    public static string Undo => Path.Combine(Temp, "undo");
    public static string Recovery => Path.Combine(Root, "recovery");
    public static string Logs => Path.Combine(Root, "logs");

    /// <summary>Öz test gibi yalıtılmış çalıştırmalar için kök klasörü değiştirir.</summary>
    public static void OverrideRoot(string root) => Root = root;

    public static void Ensure()
    {
        foreach (var d in new[] { Root, Temp, Undo, Recovery, Logs }) Directory.CreateDirectory(d);
    }

    /// <summary>Belirtilen günden eski geçici dosyaları siler. Silinen dosya sayısını döndürür.</summary>
    public static int CleanTemp(int olderThanDays, bool all = false)
    {
        int n = 0;
        try
        {
            var limit = DateTime.Now.AddDays(-olderThanDays);
            foreach (var f in Directory.EnumerateFiles(Temp, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (all || File.GetLastWriteTime(f) < limit) { File.Delete(f); n++; }
                }
                catch { /* kullanımda olabilir */ }
            }
        }
        catch { }
        return n;
    }

    public static long TempSize()
    {
        try { return Directory.EnumerateFiles(Temp, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }); }
        catch { return 0; }
    }
}

/// <summary>Basit dönen günlük dosyası. Kullanıcı belge içeriği yazılmaz.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("BİLGİ", message);
    public static void Warn(string message) => Write("UYARI", message);
    public static void Error(string message, Exception? ex = null) => Write("HATA", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var file = Path.Combine(AppPaths.Logs, $"mesly-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
                // 14 günden eski günlükleri temizle
                foreach (var old in Directory.EnumerateFiles(AppPaths.Logs, "mesly-*.log"))
                    if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-14)) File.Delete(old);
            }
        }
        catch { }
    }
}
