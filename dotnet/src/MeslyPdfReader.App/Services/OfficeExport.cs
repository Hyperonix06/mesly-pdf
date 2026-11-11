using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MeslyPdfReader.App.Services;

/// <summary>Office belgesini PDF'ye çeviren araç.</summary>
public enum OfficeEngine { None, MicrosoftOffice, LibreOffice }

/// <summary>
/// Word / Excel / PowerPoint / OpenDocument dosyalarını PDF'ye çevirir. Bilgisayarda kurulu Microsoft Office
/// (COM otomasyonu, görünmez) kullanılır; yoksa kurulu LibreOffice (başsız kip). Dönüşüm tamamen bu bilgisayarda
/// yapılır; dosya hiçbir yere gönderilmez. İkisi de yoksa açık bir hata verilir — sahte dönüşüm yapılmaz.
/// </summary>
public static class OfficeExport
{
    public static readonly string[] WordExt = { ".doc", ".docx", ".docm", ".dot", ".dotx", ".rtf", ".odt", ".txt", ".wpd" };
    public static readonly string[] ExcelExt = { ".xls", ".xlsx", ".xlsm", ".xlsb", ".ods", ".csv" };
    public static readonly string[] PowerPointExt = { ".ppt", ".pptx", ".pptm", ".pps", ".ppsx", ".odp" };

    public const string Filter =
        "Office belgeleri|*.doc;*.docx;*.docm;*.dot;*.dotx;*.rtf;*.odt;*.txt;*.xls;*.xlsx;*.xlsm;*.xlsb;*.ods;*.csv;*.ppt;*.pptx;*.pptm;*.pps;*.ppsx;*.odp" +
        "|Word belgeleri|*.doc;*.docx;*.docm;*.rtf;*.odt;*.txt|Excel çalışma kitapları|*.xls;*.xlsx;*.xlsm;*.xlsb;*.ods;*.csv|PowerPoint sunuları|*.ppt;*.pptx;*.pptm;*.pps;*.ppsx;*.odp";

    public static bool IsOfficeFile(string path)
    {
        var e = Path.GetExtension(path).ToLowerInvariant();
        return WordExt.Contains(e) || ExcelExt.Contains(e) || PowerPointExt.Contains(e);
    }

    private static string? ProgIdFor(string path)
    {
        var e = Path.GetExtension(path).ToLowerInvariant();
        if (WordExt.Contains(e)) return "Word.Application";
        if (ExcelExt.Contains(e)) return "Excel.Application";
        if (PowerPointExt.Contains(e)) return "PowerPoint.Application";
        return null;
    }

    private static bool HasProgId(string progId) => OperatingSystem.IsWindows() && Type.GetTypeFromProgID(progId) is not null;

    /// <summary>LibreOffice'in soffice.exe yolu (kayıt defteri veya varsayılan kurulum klasörü).</summary>
    public static string? LibreOfficePath()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var k = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(@"SOFTWARE\LibreOffice\UNO\InstallPath");
                    if (k?.GetValue(null) is string dir && File.Exists(Path.Combine(dir, "soffice.exe"))) return Path.Combine(dir, "soffice.exe");
                }
        }
        catch { }
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            var p = Path.Combine(pf, "LibreOffice", "program", "soffice.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Bu dosya için kullanılacak araç.</summary>
    public static OfficeEngine EngineFor(string path)
    {
        if (ProgIdFor(path) is { } id && HasProgId(id)) return OfficeEngine.MicrosoftOffice;
        return LibreOfficePath() is not null ? OfficeEngine.LibreOffice : OfficeEngine.None;
    }

    /// <summary>Kullanılabilir araçların kısa açıklaması (ayarlar / hata iletisi için).</summary>
    public static string Describe()
    {
        var parts = new List<string>();
        if (HasProgId("Word.Application")) parts.Add("Microsoft Word");
        if (HasProgId("Excel.Application")) parts.Add("Microsoft Excel");
        if (HasProgId("PowerPoint.Application")) parts.Add("Microsoft PowerPoint");
        if (LibreOfficePath() is not null) parts.Add("LibreOffice");
        return parts.Count == 0 ? "bulunamadı" : string.Join(", ", parts);
    }

    public const string NoEngineMessage =
        "Office belgesini PDF'ye çevirmek için bu bilgisayarda Microsoft Office (Word, Excel, PowerPoint) veya ücretsiz LibreOffice kurulu olmalıdır. " +
        "İkisi de bulunamadı.\n\nLibreOffice'i libreoffice.org adresinden kurduktan sonra yeniden deneyin. Mesly PDF dosyanızı hiçbir çevrim içi hizmete göndermez.";

    /// <summary>Dosyayı PDF'ye çevirir; <paramref name="pdfPath"/> başarıyla yazılmışsa döner, aksi hâlde açık iletili hata fırlatır.</summary>
    public static async Task ConvertAsync(string source, string pdfPath, CancellationToken ct = default)
    {
        source = Path.GetFullPath(source); pdfPath = Path.GetFullPath(pdfPath);
        if (!File.Exists(source)) throw new FileNotFoundException("Dosya bulunamadı.", source);
        var engine = EngineFor(source);
        if (engine == OfficeEngine.None) throw new InvalidOperationException(NoEngineMessage);
        var dir = Path.GetDirectoryName(pdfPath)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Guid.NewGuid():N}.mesly-tmp.pdf");
        try
        {
            if (engine == OfficeEngine.MicrosoftOffice) await RunStaAsync(() => ExportWithOffice(source, tmp), TimeSpan.FromMinutes(10), ct);
            else await ExportWithLibreOfficeAsync(source, tmp, ct);
            if (!File.Exists(tmp) || new FileInfo(tmp).Length < 64)
                throw new InvalidOperationException("Office dönüşümü PDF üretmedi. Dosya parola korumalı veya bozuk olabilir.");
            File.Move(tmp, pdfPath, overwrite: true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    // ───────────── Microsoft Office (COM, geç bağlama) ─────────────

    private static void ExportWithOffice(string src, string pdf)
    {
        var progId = ProgIdFor(src)!;
        var type = Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException(NoEngineMessage);
        dynamic? app = null; dynamic? docs = null; dynamic? doc = null;
        try
        {
            app = Activator.CreateInstance(type)!;
            switch (progId)
            {
                case "Word.Application":
                    app.Visible = false;
                    app.DisplayAlerts = 0; // wdAlertsNone
                    docs = app.Documents;
                    // Parola korumalı belgede parola penceresi açılmasın: boş olmayan sahte parola → hata.
                    doc = docs.Open(src, false, true, false, "\u0001mesly", "\u0001mesly", true, Type.Missing, Type.Missing, Type.Missing, Type.Missing, false);
                    // ExportAsFixedFormat(OutputFileName, wdExportFormatPDF=17, OpenAfterExport, OptimizeFor=Print, Range=AllDocument,
                    //   From, To, Item=DocumentContent, IncludeDocProps, KeepIRM, CreateBookmarks=HeadingBookmarks(1), DocStructureTags, BitmapMissingFonts, UseISO19005_1)
                    doc.ExportAsFixedFormat(pdf, 17, false, 0, 0, 1, 1, 0, true, true, 1, true, true, false);
                    doc.Close(0);
                    doc = null;
                    break;
                case "Excel.Application":
                    app.Visible = false;
                    app.DisplayAlerts = false;
                    app.AskToUpdateLinks = false;
                    docs = app.Workbooks;
                    doc = docs.Open(src, 0, true, Type.Missing, "\u0001mesly", "\u0001mesly", true);
                    // ExportAsFixedFormat(xlTypePDF=0, Filename, Quality=Standard, IncludeDocProperties, IgnorePrintAreas=false, From, To, OpenAfterPublish)
                    doc.ExportAsFixedFormat(0, pdf, 0, true, false, Type.Missing, Type.Missing, false);
                    doc.Close(false);
                    doc = null;
                    break;
                default: // PowerPoint
                    app.DisplayAlerts = 1; // ppAlertsNone
                    docs = app.Presentations;
                    // Open(FileName, ReadOnly=msoTrue, Untitled=msoFalse, WithWindow=msoFalse)
                    doc = docs.Open(src, -1, 0, 0);
                    doc.SaveAs(pdf, 32); // ppSaveAsPDF
                    doc.Close();
                    doc = null;
                    break;
            }
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException($"{OfficeName(progId)} dosyayı PDF'ye çeviremedi: {ex.Message}\nDosya parola korumalı, bozuk veya başka bir programda açık olabilir.", ex);
        }
        finally
        {
            try { if (doc is not null) { if (progId == "Excel.Application") doc.Close(false); else if (progId == "Word.Application") doc.Close(0); else doc.Close(); } } catch { }
            try { app?.Quit(); } catch { }
            Release(doc); Release(docs); Release(app);
            GC.Collect(); GC.WaitForPendingFinalizers();
        }
    }

    private static string OfficeName(string progId) => progId switch
    {
        "Word.Application" => "Microsoft Word",
        "Excel.Application" => "Microsoft Excel",
        _ => "Microsoft PowerPoint",
    };

    private static void Release(object? o)
    {
        if (o is not null && OperatingSystem.IsWindows() && Marshal.IsComObject(o)) try { Marshal.FinalReleaseComObject(o); } catch { }
    }

    /// <summary>Office otomasyonu STA iş parçacığı ister; uzun süren veya takılan işlem zaman aşımıyla kesilir.</summary>
    private static Task RunStaAsync(Action work, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { work(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { IsBackground = true, Name = "Mesly Office dönüşümü" };
        if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return WaitAsync(tcs.Task, timeout, ct);
    }

    private static async Task WaitAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        try { await task.WaitAsync(timeout, ct); }
        catch (TimeoutException) { throw new InvalidOperationException("Office dönüşümü zaman aşımına uğradı (10 dakika). Office'te açık bir uyarı penceresi olabilir."); }
    }

    // ───────────── LibreOffice (başsız) ─────────────

    private static async Task ExportWithLibreOfficeAsync(string src, string pdf, CancellationToken ct)
    {
        var soffice = LibreOfficePath() ?? throw new InvalidOperationException(NoEngineMessage);
        var outDir = Path.Combine(Path.GetTempPath(), "mesly-office", Guid.NewGuid().ToString("N"));
        // Ayrı profil: kullanıcının açık LibreOffice oturumunu etkilemez, kilitlenmez.
        var profile = new Uri(Path.Combine(outDir, "profile")).AbsoluteUri;
        Directory.CreateDirectory(outDir);
        try
        {
            var psi = new ProcessStartInfo(soffice)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            foreach (var a in new[] { $"-env:UserInstallation={profile}", "--headless", "--norestore", "--nolockcheck", "--convert-to", "pdf", "--outdir", outDir, src })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("LibreOffice başlatılamadı.");
            var errTask = p.StandardError.ReadToEndAsync(ct);
            _ = p.StandardOutput.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                if (ct.IsCancellationRequested) throw;
                throw new InvalidOperationException("LibreOffice dönüşümü zaman aşımına uğradı (10 dakika).");
            }
            var produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(src) + ".pdf");
            if (!File.Exists(produced))
            {
                string err = "";
                try { err = (await errTask).Trim(); } catch { }
                throw new InvalidOperationException("LibreOffice dosyayı PDF'ye çeviremedi." + (err.Length > 0 ? "\n" + err : "") + "\nDosya parola korumalı veya bozuk olabilir.");
            }
            File.Move(produced, pdf, overwrite: true);
        }
        finally { try { Directory.Delete(outDir, true); } catch { } }
    }
}
