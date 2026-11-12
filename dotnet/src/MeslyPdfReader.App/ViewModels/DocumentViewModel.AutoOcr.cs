using MeslyPdfReader.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeslyPdfReader.App.Controls;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Ocr;

namespace MeslyPdfReader.App.ViewModels;

/// <summary>
/// Otomatik (arka plan) tanıma: taranmış, metni olmayan sayfalar belge açılınca kendiliğinden düzeltilir (yön, eğiklik,
/// kenarlar) ve tanınır; önce ekrandaki sayfa. Belge bu sırada kullanılabilir. Kullanıcı bir işlem başlatınca iş
/// sayfa sınırında duraklar, işlem bitince kalan sayfalarla sürer. Tamamı tek adımda geri alınabilir (Ctrl+Z).
/// Her şey bu bilgisayarda yapılır.
/// </summary>
public sealed partial class DocumentViewModel
{
    [ObservableProperty] private bool _isAutoOcrRunning;
    [ObservableProperty] private string _autoOcrText = "";
    [ObservableProperty] private double _autoOcrProgress;

    private Task? _autoTask;
    private CancellationTokenSource? _autoCts;
    private volatile bool _autoPauseRequested;
    private bool _autoPausedForBusy, _autoStoppedByUser;

    /// <summary>Son otomatik tanımada işlenen sayfa sayısı (öz test için).</summary>
    public int AutoOcrPagesDone { get; private set; }
    public Task AutoOcrTask => _autoTask ?? Task.CompletedTask;

    /// <summary>Uyarı şeridi: taranmış sayfa ve otomatik tanıma çalışmıyor.</summary>
    public bool ShowScanBanner => CurrentPageIsScanned && !IsAutoOcrRunning;

    partial void OnCurrentPageIsScannedChanged(bool value) => OnPropertyChanged(nameof(ShowScanBanner));
    partial void OnIsAutoOcrRunningChanged(bool value) => OnPropertyChanged(nameof(ShowScanBanner));

    /// <summary>Ayar açıksa ve belgede taranmış sayfa varsa otomatik tanımayı başlatır.</summary>
    private ImageCorrection? _autoCorrection;

    /// <param name="correction">Verilirse ayarlar yerine bu düzeltme uygulanır (ör. taramada düzeltme zaten yapıldıysa None).</param>
    public void StartAutoOcr(bool force = false, ImageCorrection? correction = null)
    {
        _autoCorrection = correction;
        if (_disposed || (!force && (!_settings.OcrAutoOnOpen || _autoStoppedByUser))) return;
        if (_autoTask is { IsCompleted: false }) return;
        if (OcrEngine.Unavailable() is not null) return; // tanıma bileşeni yoksa şerit “Metni tanı” ile açık hatayı gösterir
        _autoPauseRequested = false;
        _autoCts = new CancellationTokenSource();
        _autoTask = RunAutoOcrAsync(_autoCts.Token);
    }

    [RelayCommand]
    private async Task StopAutoOcr()
    {
        _autoStoppedByUser = true;
        _autoCts?.Cancel();
        await AutoOcrTask;
    }

    private async Task RunAutoOcrAsync(CancellationToken ct)
    {
        var done = new List<OcrPageResult>();
        bool checkpointed = false;
        try
        {
            await WaitWhileBusyAsync(ct);
            if (_autoPauseRequested) return;
            var remaining = await ConvertWorkflow.ScannedPagesAsync(Document, Enumerable.Range(0, PageCount).ToList(), ct);
            if (remaining.Count == 0 || ct.IsCancellationRequested) return;
            int total = remaining.Count;
            IsAutoOcrRunning = true;
            AutoOcrProgress = 0;
            AutoOcrText = $"Taranmış {total} sayfa otomatik olarak düzeltilip tanınacak…";
            await WaitWhileBusyAsync(ct);
            if (_autoPauseRequested) return;
            await CheckpointAsync(); // tamamı tek adımda geri alınabilir
            checkpointed = true;
            CanUndo = true;
            var opts = OcrRunOptions.From(_settings);
            if (_autoCorrection is { } ac) opts = opts with { Correction = ac };
            while (remaining.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (_autoPauseRequested) return;
                // Önce kullanıcının baktığı sayfa, sonra onu izleyenler.
                int page = remaining.Contains(CurrentPage) ? CurrentPage
                    : remaining.Where(p => p > CurrentPage).DefaultIfEmpty(remaining[0]).First();
                remaining.Remove(page);
                int n = total - remaining.Count;
                // Kullanıcı işlemi sürüyorsa bitmesini bekle; sayfa bu arada (ör. elle) tanındıysa atla.
                await WaitWhileBusyAsync(ct);
                if (_autoPauseRequested) return;
                if (await Document.CountRealTextCharsAsync(page) >= 10 || await Document.HasOcrLayerAsync(page)) continue;
                AutoOcrText = $"Taranmış sayfalar otomatik düzeltiliyor ve tanınıyor — sayfa {page + 1} ({n}/{total})";
                var res = await OcrWorkflow.RecognizeAsync(Document, new[] { page }, opts,
                    new Progress<double>(x => AutoOcrProgress = (n - 1 + x) / total * 100), ct);
                foreach (var r in res)
                {
                    done.Add(r);
                    if (r.Ocr is not null) _ocrResults[r.Page] = r.Ocr;
                }
                AutoOcrPagesDone = done.Count(r => r.Ocr is not null);
            }
            StatusText = $"Otomatik tanıma: {OcrWorkflow.Summarize(done)} Kaydetmek için Ctrl+S, geri almak için Ctrl+Z.";
        }
        catch (OperationCanceledException)
        {
            if (_autoStoppedByUser)
                StatusText = done.Count > 0
                    ? $"Otomatik tanıma durduruldu ({done.Count} sayfa tamamlandı). Kalan sayfalar için “Metni tanı”yı kullanın."
                    : "Otomatik tanıma durduruldu.";
        }
        catch (Exception ex)
        {
            Log.Error("Otomatik tanıma", ex);
            StatusText = "Otomatik tanıma yapılamadı: " + ex.Message;
        }
        finally
        {
            // Hiçbir sayfa değişmediyse boş geri alma adımı bırakma.
            if (checkpointed && done.Count == 0 && !Document.IsModified && _undo.TryPop(out var snap)) TryDelete(snap.FilePath);
            CanUndo = _undo.Count > 0;
            IsAutoOcrRunning = false;
            AutoOcrProgress = 0;
            OnPropertyChanged(nameof(HasOcrResults)); OnPropertyChanged(nameof(LowConfidenceCount));
            _bannerCheckRev = -1;
            _ = UpdateScanBannerAsync();
        }
    }

    /// <summary>Kullanıcı işlemi (IsBusy) sürerken belgeye dokunmamak için bekler.</summary>
    private async Task WaitWhileBusyAsync(CancellationToken ct)
    {
        while (IsBusy && !_autoPauseRequested) await Task.Delay(150, ct);
    }

    protected override async Task BeforeBusyAsync()
    {
        if (_autoTask is not { IsCompleted: false } t) return;
        BusyText = "Arka plandaki metin tanıma duraklatılıyor…";
        _autoPauseRequested = true;
        _autoPausedForBusy = true;
        await t; // geçerli sayfa bitince durur (yarım sayfa kalmaz)
    }

    protected override void AfterBusy()
    {
        if (!_autoPausedForBusy) return;
        _autoPausedForBusy = false;
        StartAutoOcr();
    }

    private void StopAutoOcrForDispose()
    {
        try { _autoCts?.Cancel(); } catch { }
    }
}
