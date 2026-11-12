using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.ViewModels;

/// <summary>Aşama 5: PDF → Word / Excel dönüştürme ve PDF sıkıştırma.</summary>
public sealed partial class DocumentViewModel
{
    [RelayCommand] private Task ConvertToWord() => ConvertAsync(ConvertFormat.Word);
    [RelayCommand] private Task ConvertToExcel() => ConvertAsync(ConvertFormat.Excel);
    [RelayCommand] private Task ConvertToPowerPoint() => ConvertAsync(ConvertFormat.PowerPoint);

    /// <summary>Son dönüşümün sonucu (öz test ve durum çubuğu için).</summary>
    public ConvertResult? LastConvert { get; private set; }

    public async Task<ConvertResult?> ConvertAsync(ConvertFormat format, ConvertRequest? request = null, string? target = null)
    {
        var all = Enumerable.Range(0, PageCount).ToList();
        if (request is null)
        {
            List<int> scanned;
            try { scanned = await ConvertWorkflow.ScannedPagesAsync(Document, all); } catch { scanned = new(); }
            request = ConvertDialog.Ask(format, PageCount, PageRange.Format(SelectedPages.Count > 1 ? TargetPages() : all), scanned.Count, _settings);
            if (request is null) return null;
        }
        if (target is null)
        {
            var (ext, filter, caption) = format switch
            {
                ConvertFormat.Word => (".docx", "Word belgesi (*.docx)|*.docx", "Word belgesi olarak kaydet"),
                ConvertFormat.PowerPoint => (".pptx", "PowerPoint sunusu (*.pptx)|*.pptx", "PowerPoint sunusu olarak kaydet"),
                _ => (".xlsx", "Excel çalışma kitabı (*.xlsx)|*.xlsx", "Excel çalışma kitabı olarak kaydet"),
            };
            target = FileDialogs.SaveFile(Path.GetFileNameWithoutExtension(Title) + ext, filter,
                Document.FilePath is { } p ? Path.GetDirectoryName(p) : null, caption);
            if (target is null) return null;
        }
        ConvertResult? result = null;
        var kind = format switch { ConvertFormat.Word => "Word belgesine", ConvertFormat.PowerPoint => "PowerPoint sunusuna", ConvertFormat.Excel => "Excel çalışma kitabına", ConvertFormat.Image => "resme", _ => "metne" };
        var ok = await RunBusyAsync($"{kind} dönüştürülüyor ({request.Pages.Count} sayfa)…", async (progress, ct) =>
        {
            result = await ConvertWorkflow.ConvertAsync(Document, target, request, progress, ct);
        }, ConvertWorkflow.TaskKind(format), Title, () => target);
        if (!ok || result is null) return null;
        LastConvert = result;
        StatusText = $"{Path.GetFileName(target)}: {ConvertWorkflow.Summarize(result)}";
        if (DialogBase.Automation is null)
        {
            if (_settings.ConvertOpenAfter)
            {
                try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
                catch (Exception ex)
                {
                    MessageDialog.Show("Dosya açılamadı", $"{ConvertWorkflow.Summarize(result)}\n\nDosya kaydedildi ancak açılamadı (bu dosya türü için kayıtlı bir uygulama olmayabilir):\n{ex.Message}", MessageKind.Warning);
                }
            }
            else OfferOpenFolder(ConvertWorkflow.Summarize(result), Path.GetDirectoryName(target)!);
        }
        return result;
    }

    /// <summary>Son sıkıştırmanın sonucu (öz test için).</summary>
    public CompressReport? LastCompress { get; private set; }

    [RelayCommand] private Task Compress() => CompressAsync();

    public async Task<CompressReport?> CompressAsync(CompressOptions? options = null, string? target = null)
    {
        long size = Document.FilePath is { } fp && File.Exists(fp) && !Document.IsModified ? new FileInfo(fp).Length : 0;
        if (options is null)
        {
            options = CompressDialog.Ask(Title, size, _settings);
            if (options is null) return null;
        }
        if (target is null)
        {
            target = FileDialogs.SaveFile(Path.GetFileNameWithoutExtension(Title) + " (sıkıştırılmış).pdf", FileDialogs.PdfFilter,
                Document.FilePath is { } p ? Path.GetDirectoryName(p) : null, "Sıkıştırılmış PDF'yi kaydet");
            if (target is null) return null;
        }
        if (Document.FilePath is { } src && PathsEqual(src, target))
        {
            MessageDialog.Show("Başka bir ad seçin", "Sıkıştırılmış belge özgün dosyanın üzerine yazılamaz. Özgün dosya güvende kalsın diye farklı bir ad seçin.", MessageKind.Warning);
            return null;
        }
        CompressReport? report = null;
        var ok = await RunBusyAsync("PDF sıkıştırılıyor…", async (progress, ct) =>
        {
            // Kaydedilmemiş düzenlemeler de dahil olsun diye her zaman görünen belgenin kopyasından çalışılır.
            Directory.CreateDirectory(AppPaths.Temp);
            var tmp = Path.Combine(AppPaths.Temp, $"compress-{Guid.NewGuid():N}.pdf");
            try
            {
                if (Document.FilePath is { } f && File.Exists(f) && !Document.IsModified) File.Copy(f, tmp);
                else await Document.SaveCopyAsync(tmp);
                report = await PdfTools.CompressFileAsync(tmp, target, options, Document.Password, progress, ct);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }, "compress", Title, () => target);
        if (!ok || report is null) return null;
        LastCompress = report;
        if (!report.Written)
        {
            StatusText = "Sıkıştırma kazanç sağlamadı; dosya yazılmadı.";
            if (DialogBase.Automation is null)
                MessageDialog.Show("Belge zaten küçük", $"{report.ImagesSeen} görselden {report.ImagesRecompressed} tanesi yeniden kodlandı, ancak sonuç ({CompressDialog.FormatSize(report.BytesAfter)}) özgün dosyadan ({CompressDialog.FormatSize(report.BytesBefore)}) anlamlı ölçüde küçük olmadı. Yeni dosya yazılmadı.", MessageKind.Info);
            return report;
        }
        var msg = $"{CompressDialog.FormatSize(report.BytesBefore)} → {CompressDialog.FormatSize(report.BytesAfter)} (%{(1 - report.Ratio) * 100:0} küçüldü). {report.ImagesRecompressed}/{report.ImagesSeen} görsel yeniden kodlandı.";
        StatusText = "Sıkıştırıldı: " + msg;
        if (DialogBase.Automation is null &&
            MessageDialog.Confirm("Sıkıştırma tamamlandı", msg + "\n\nSıkıştırılmış belge açılsın mı?", "Aç", "Kapat", MessageKind.Success))
            await Main.OpenFilesAsync(new[] { target });
        return report;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
