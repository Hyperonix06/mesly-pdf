using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeslyPdfReader.App.Controls;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Core;
using MeslyPdfReader.Ocr;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.ViewModels;

/// <summary>Aşama 4: metin tanıma (OCR), görüntü düzeltme ve tanıma doğrulama.</summary>
public sealed partial class DocumentViewModel
{
    /// <summary>Bu oturumda tanınan sayfaların sonuçları (doğrulama için).</summary>
    private readonly Dictionary<int, OcrPage> _ocrResults = new();

    [ObservableProperty] private bool _currentPageIsScanned;
    /// <summary>“Sayfaları Düzenle” sekmesi: orta alanda sayfa ızgarası gösterilir.</summary>
    [ObservableProperty] private bool _isOrganizeMode;
    [ObservableProperty] private string _scanBannerText = "";
    public bool HasOcrResults => _ocrResults.Count > 0;
    public int LowConfidenceCount => _ocrResults.Values.Sum(p => p.Words.Count(w => w.Confidence < 60));

    private int _bannerCheckRev = -1, _bannerCheckPage = -1;

    /// <summary>Geçerli sayfa taranmış (metinsiz) bir görüntüyse uyarı şeridini gösterir.</summary>
    private async Task UpdateScanBannerAsync()
    {
        int page = CurrentPage, rev = Document.Revision;
        if (page == _bannerCheckPage && rev == _bannerCheckRev) return;
        _bannerCheckPage = page; _bannerCheckRev = rev;
        try
        {
            if (page < 0 || page >= PageCount) { CurrentPageIsScanned = false; return; }
            bool scanned = await Document.CountRealTextCharsAsync(page) < 10 && !await Document.HasOcrLayerAsync(page) && await Document.GetMainImageAsync(page) is not null;
            if (page != CurrentPage) return;
            CurrentPageIsScanned = scanned;
            ScanBannerText = Tool == ViewerTool.Edit
                ? "Bu sayfa taranmış bir görüntü. Metnini düzenleyebilmek için önce metni tanıyın."
                : "Bu sayfa taranmış bir görüntü; metni aranamaz ve kopyalanamaz. Metni tanıyarak aranabilir yapabilirsiniz.";
        }
        catch { CurrentPageIsScanned = false; }
    }

    [RelayCommand] private void DismissScanBanner() => CurrentPageIsScanned = false;

    /// <summary>İlerlemeli düzenleme: geri alma noktası + iptal edilebilir iş.</summary>
    private Task<bool> EditProgressAsync(string text, Func<IProgress<double>, CancellationToken, Task> work, Func<string> status) => RunBusyAsync(text, async (p, ct) =>
    {
        await CheckpointAsync();
        try { await work(p, ct); RefreshAfterChange(); }
        catch
        {
            if (_undo.TryPop(out var snap)) await Document.RestoreSnapshotAsync(snap, Document.IsModified);
            CanUndo = _undo.Count > 0;
            RefreshAfterChange();
            throw;
        }
        StatusText = status();
    });

    [RelayCommand]
    private Task RecognizeText() => RunOcrAsync(ocr: true, pages: null);

    /// <summary>Yalnız geçerli sayfayı tanır (uyarı şeridindeki düğme).</summary>
    [RelayCommand]
    private Task RecognizeCurrentPage() => RunOcrAsync(ocr: true, pages: new[] { CurrentPage }, ask: false);

    [RelayCommand]
    private Task CorrectImages() => RunOcrAsync(ocr: false, pages: null);

    public async Task<IReadOnlyList<OcrPageResult>?> RunOcrAsync(bool ocr, IReadOnlyList<int>? pages, bool ask = true)
    {
        if (ocr && OcrEngine.Unavailable() is { } why) { MessageDialog.Show("Metin tanıma kullanılamıyor", why, MessageKind.Error); return null; }
        var opts = OcrRunOptions.From(_settings);
        IReadOnlyList<int> target = pages ?? TargetPages();
        if (ask)
        {
            var r = OcrDialog.Ask(ocr, PageCount, PageRange.Format(pages ?? (SelectedPages.Count > 1 ? TargetPages() : Enumerable.Range(0, PageCount).ToList())), _settings);
            if (r is null) return null;
            target = r.Value.Pages; opts = r.Value.Options;
        }
        IReadOnlyList<OcrPageResult> results = Array.Empty<OcrPageResult>();
        var ok = await EditProgressAsync(ocr ? $"Metin tanınıyor ({target.Count} sayfa)…" : $"Görüntüler düzeltiliyor ({target.Count} sayfa)…", async (p, ct) =>
        {
            results = await OcrWorkflow.RecognizeAsync(Document, target, opts, p, ct, ocr);
        }, () => OcrWorkflow.Summarize(results));
        if (!ok) return null;
        foreach (var r in results) if (r.Ocr is not null) _ocrResults[r.Page] = r.Ocr;
        OnPropertyChanged(nameof(HasOcrResults)); OnPropertyChanged(nameof(LowConfidenceCount));
        _bannerCheckRev = -1;
        await UpdateScanBannerAsync();
        return results;
    }

    [RelayCommand]
    private async Task VerifyOcr()
    {
        if (_ocrResults.Count == 0) { StatusText = "Doğrulanacak tanıma sonucu yok. Önce “Metni tanı” ile sayfaları tanıyın."; return; }
        var changed = await OcrVerifyDialog.ShowAsync(Document, _ocrResults, _settings.OcrDpi);
        if (changed.Count == 0) return;
        await EditAsync("Düzeltmeler uygulanıyor…", async () =>
        {
            foreach (var (page, ocr) in changed)
            {
                _ocrResults[page] = ocr;
                await Document.AddOcrLayerAsync(page, ocr, OcrWorkflow.LayerFont);
            }
        }, $"{changed.Count} sayfanın metin katmanı düzeltildi.");
        OnPropertyChanged(nameof(LowConfidenceCount));
    }

    [RelayCommand]
    private async Task RemoveOcrLayer()
    {
        int n = 0;
        if (await EditAsync("Metin katmanı kaldırılıyor…", async () => n = await Document.RemoveOcrLayerAsync(Enumerable.Range(0, PageCount)), ""))
        {
            _ocrResults.Clear(); OnPropertyChanged(nameof(HasOcrResults));
            StatusText = n > 0 ? $"Tanınan metin katmanı kaldırıldı ({n} sözcük)." : "Mesly PDF Reader ile eklenmiş metin katmanı yok.";
            _bannerCheckRev = -1;
            await UpdateScanBannerAsync();
        }
    }
}
