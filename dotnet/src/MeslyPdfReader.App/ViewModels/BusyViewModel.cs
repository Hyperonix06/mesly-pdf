using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Core;
using MeslyPdfReader.Data;

namespace MeslyPdfReader.App.ViewModels;

/// <summary>Uzun işlemleri arka planda, ilerleme ve iptal ile yürüten ortak taban.</summary>
public abstract partial class BusyViewModel : ObservableObject
{
    private CancellationTokenSource? _busyCts;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private double _busyProgress;
    [ObservableProperty] private bool _busyIndeterminate = true;
    [ObservableProperty] private string _statusText = "Hazır";

    protected AppDatabase? Db { get; init; }

    [RelayCommand]
    private void CancelBusy() => _busyCts?.Cancel();

    /// <summary>
    /// İşlemi yürütür; hataları kullanıcıya Türkçe gösterir, uygulamayı kapatmaz.
    /// <paramref name="historyKind"/> verilirse görev geçmişine yazılır.
    /// </summary>
    protected async Task<bool> RunBusyAsync(string text, Func<IProgress<double>, CancellationToken, Task> work, string? historyKind = null, string? historyInput = null, Func<string?>? historyOutput = null)
    {
        if (IsBusy) return false;
        _busyCts = new CancellationTokenSource();
        BusyText = text; BusyProgress = 0; BusyIndeterminate = true; IsBusy = true;
        long? taskId = null;
        if (historyKind is not null && Db is not null) try { taskId = Db.StartTask(historyKind, historyInput ?? ""); } catch { }
        var progress = new Progress<double>(p => { BusyIndeterminate = false; BusyProgress = Math.Clamp(p * 100, 0, 100); });
        try
        {
            await work(progress, _busyCts.Token);
            if (taskId is { } id) try { Db!.FinishTask(id, "ok", historyOutput?.Invoke(), null); } catch { }
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "İşlem iptal edildi.";
            if (taskId is { } id) try { Db!.FinishTask(id, "cancelled", null, "İptal edildi"); } catch { }
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(text, ex);
            if (taskId is { } id) try { Db!.FinishTask(id, "error", null, ex.Message); } catch { }
            ShowError(text.TrimEnd('…', '.'), ex);
            return false;
        }
        finally
        {
            IsBusy = false;
            _busyCts.Dispose(); _busyCts = null;
        }
    }

    public static void ShowError(string operation, Exception ex)
    {
        var msg = ex switch
        {
            PdfException p => p.Message,
            FormatException f => f.Message,
            UnauthorizedAccessException => "Dosyaya erişim izni yok. Dosya salt okunur olabilir ya da başka bir program kullanıyor olabilir.",
            System.IO.IOException io => "Dosya işlemi başarısız: " + io.Message,
            OutOfMemoryException => "Bu işlem için yeterli bellek yok. Daha küçük bir sayfa aralığı veya daha düşük çözünürlük deneyin.",
            _ => ex.Message,
        };
        MessageDialog.Show($"{operation} başarısız", msg, MessageKind.Error, ex is PdfException or FormatException ? null : ex.ToString());
    }
}
