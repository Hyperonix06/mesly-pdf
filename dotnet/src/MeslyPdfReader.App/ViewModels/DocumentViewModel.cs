using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeslyPdfReader.App.Controls;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Core;
using MeslyPdfReader.Data;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.ViewModels;

public enum LeftPanelKind { None, Thumbnails, Bookmarks, Search }

public sealed partial class DocumentViewModel : BusyViewModel, IDisposable
{
    private const int MaxUndo = 25;
    private readonly AppSettings _settings;
    private readonly AppDatabase _db;
    private readonly Stack<DocumentSnapshot> _undo = new(), _redo = new();
    private CancellationTokenSource? _searchCts;
    private bool _disposed;

    public PdfDocument Document { get; }
    public Guid Id { get; } = Guid.NewGuid();
    /// <summary>Görüntüleyiciye erişim (kopyalama, seçim, yazdırma için). Görünüm yüklenince atanır.</summary>
    public PdfViewer? Viewer { get; set; }
    public MainViewModel Main { get; }

    public DocumentViewModel(MainViewModel main, PdfDocument document, AppSettings settings, AppDatabase db)
    {
        Main = main; Document = document; _settings = settings; _db = db; Db = db;
        _zoomMode = settings.DefaultZoomMode switch
        {
            ZoomModeChoice.FitPage => ZoomMode.FitPage,
            ZoomModeChoice.ActualSize => ZoomMode.ActualSize,
            ZoomModeChoice.Custom => ZoomMode.Custom,
            _ => ZoomMode.FitWidth,
        };
        _zoom = settings.DefaultZoom;
        _layout = settings.DefaultLayout switch
        {
            LayoutChoice.SinglePage => PageLayoutMode.SinglePage,
            LayoutChoice.TwoPage => PageLayoutMode.TwoPage,
            LayoutChoice.TwoPageContinuous => PageLayoutMode.TwoPageContinuous,
            _ => PageLayoutMode.Continuous,
        };
        _darkMode = settings.DarkReading;
        _leftPanel = settings.ShowThumbnails ? LeftPanelKind.Thumbnails : LeftPanelKind.None;
        Document.Changed += OnDocumentChanged;
        RefreshAfterChange();
        RestoreState();
        _ = LoadBookmarksAsync();
        _ = LoadInfoAsync();
        _ = UpdateScanBannerAsync();
    }

    // ─────────────── Görünüm durumu ───────────────

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private int _pageCount;
    [ObservableProperty] private bool _isModified;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PageNumber), nameof(PageInfo))] private int _currentPage;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ZoomPercent))] private double _zoom = 1;
    [ObservableProperty] private ZoomMode _zoomMode = ZoomMode.FitWidth;
    [ObservableProperty] private PageLayoutMode _layout = PageLayoutMode.Continuous;
    [ObservableProperty] private int _viewRotation;
    [ObservableProperty] private bool _darkMode;
    [ObservableProperty] private ViewerTool _tool = ViewerTool.Select;
    [ObservableProperty] private LeftPanelKind _leftPanel = LeftPanelKind.Thumbnails;
    [ObservableProperty] private bool _showProperties;
    [ObservableProperty] private DocumentInfo? _info;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private bool _canRedo;
    /// <summary>Bu sekme etkin mi (görünüm görünürlüğü için).</summary>
    [ObservableProperty] private bool _isActive;

    public ObservableCollection<ThumbnailItem> Thumbnails { get; } = new();
    public ObservableCollection<BookmarkNode> Bookmarks { get; } = new();
    public List<int> SelectedPages { get; } = new();

    public string ZoomPercent => $"%{Math.Round(Zoom * 100)}";
    public string EncryptionText => Info is null ? "—" : Info.IsEncrypted ? "Parola korumalı" : "Yok";

    partial void OnInfoChanged(DocumentInfo? value) => OnPropertyChanged(nameof(EncryptionText));

    private (LeftPanelKind Left, bool Props)? _beforeFullScreen;

    /// <summary>Tam ekranda yan panelleri gizler, çıkışta eski haline getirir.</summary>
    public void SetFullScreenPanels(bool fullScreen)
    {
        if (fullScreen) { _beforeFullScreen = (LeftPanel, ShowProperties); LeftPanel = LeftPanelKind.None; ShowProperties = false; }
        else if (_beforeFullScreen is { } b) { LeftPanel = b.Left; ShowProperties = b.Props; _beforeFullScreen = null; }
    }
    public string FileSizeText => RecentItem.FormatSize(Document.SourceLength);
    public string? FilePath => Document.FilePath;

    /// <summary>1 tabanlı sayfa numarası (sayfa kutusu bağlaması).</summary>
    public int PageNumber
    {
        get => CurrentPage + 1;
        set => CurrentPage = Math.Clamp(value - 1, 0, Math.Max(0, PageCount - 1));
    }

    public string PageInfo
    {
        get
        {
            if (PageCount == 0) return "";
            var s = Document.GetPageSize(Math.Clamp(CurrentPage, 0, PageCount - 1));
            return $"{PaperSize.Describe(s)} · {PaperSize.PtToMm(s.Width):0} × {PaperSize.PtToMm(s.Height):0} mm";
        }
    }

    partial void OnCurrentPageChanged(int value)
    {
        foreach (var t in Thumbnails) if (t.IsCurrent != (t.Index == value)) t.IsCurrent = t.Index == value;
        _ = UpdateScanBannerAsync();
    }

    partial void OnDarkModeChanged(bool value) => _settings.DarkReading = value;

    partial void OnLeftPanelChanged(LeftPanelKind value)
    {
        if (value is LeftPanelKind.Thumbnails or LeftPanelKind.None) _settings.ShowThumbnails = value == LeftPanelKind.Thumbnails;
    }

    private void OnDocumentChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(RefreshAfterChange);

    private int _refreshedRevision = -1;

    private void RefreshAfterChange()
    {
        if (_disposed || _refreshedRevision == Document.Revision) return;
        _refreshedRevision = Document.Revision;
        Title = Document.DisplayName;
        IsModified = Document.IsModified;
        PageCount = Document.PageCount;
        if (CurrentPage >= PageCount) CurrentPage = Math.Max(0, PageCount - 1);
        Thumbnails.Clear();
        for (int i = 0; i < PageCount; i++) Thumbnails.Add(new ThumbnailItem(Document, i) { IsCurrent = i == CurrentPage });
        OnPropertyChanged(nameof(PageInfo));
        OnPropertyChanged(nameof(FileSizeText));
        OnPropertyChanged(nameof(FilePath));
        CanUndo = _undo.Count > 0; CanRedo = _redo.Count > 0;
    }

    private void RestoreState()
    {
        if (!_settings.RememberLastPage || Document.FilePath is null) return;
        try
        {
            var st = _db.GetDocumentState(Document.FilePath);
            if (st is not null && st.Page < PageCount) CurrentPage = st.Page;
        }
        catch (Exception ex) { Log.Warn("Belge durumu okunamadı: " + ex.Message); }
    }

    public void SaveState()
    {
        if (_settings.DefaultZoomMode == ZoomModeChoice.Custom) _settings.DefaultZoom = Zoom;
        if (!_settings.RememberLastPage || Document.FilePath is null) return;
        try { _db.SaveDocumentState(Document.FilePath, new DocumentState(CurrentPage, Zoom, ZoomMode.ToString())); }
        catch (Exception ex) { Log.Warn("Belge durumu yazılamadı: " + ex.Message); }
    }

    private async Task LoadBookmarksAsync()
    {
        try
        {
            var list = await Document.GetBookmarksAsync();
            Bookmarks.Clear();
            foreach (var b in list) Bookmarks.Add(BookmarkNode.From(b, null));
            if (Bookmarks.Count > 0 && Bookmarks.Count <= 12) foreach (var b in Bookmarks) b.IsExpanded = true;
        }
        catch (Exception ex) { Log.Warn("Yer imleri okunamadı: " + ex.Message); }
    }

    private async Task LoadInfoAsync()
    {
        try { Info = await Document.GetInfoAsync(); } catch (Exception ex) { Log.Warn("Belge bilgisi: " + ex.Message); }
    }

    // ─────────────── Görünüm komutları ───────────────

    [RelayCommand] private void ZoomIn() => Viewer?.ZoomIn();
    [RelayCommand] private void ZoomOut() => Viewer?.ZoomOut();
    [RelayCommand] private void FitWidth() => ZoomMode = ZoomMode.FitWidth;
    [RelayCommand] private void FitPage() => ZoomMode = ZoomMode.FitPage;
    [RelayCommand] private void ActualSize() => ZoomMode = ZoomMode.ActualSize;
    [RelayCommand] private void SetZoomPercent(string percent) { if (double.TryParse(percent, out var p)) Viewer?.ZoomTo(p / 100.0, null); }
    [RelayCommand] private void SetLayout(PageLayoutMode mode) => Layout = mode;
    [RelayCommand] private void RotateViewRight() => ViewRotation = (ViewRotation + 1) % 4;
    [RelayCommand] private void RotateViewLeft() => ViewRotation = (ViewRotation + 3) % 4;
    [RelayCommand] private void NextPage() => Viewer?.NextPage();
    [RelayCommand] private void PreviousPage() => Viewer?.PreviousPage();
    [RelayCommand] private void FirstPage() => CurrentPage = 0;
    [RelayCommand] private void LastPage() => CurrentPage = PageCount - 1;
    [RelayCommand] private void ToggleDark() => DarkMode = !DarkMode;
    [RelayCommand] private void ShowPanel(LeftPanelKind kind) => LeftPanel = LeftPanel == kind ? LeftPanelKind.None : kind;
    [RelayCommand] private void ToggleProperties() => ShowProperties = !ShowProperties;
    [RelayCommand] private void SetTool(ViewerTool tool) => Tool = tool;

    [RelayCommand]
    private void GoToPagePrompt()
    {
        var v = InputDialog.Ask("Sayfaya git", $"Sayfa numarası (1–{PageCount})", (CurrentPage + 1).ToString(), validate: s =>
            int.TryParse(s, out var n) && n >= 1 && n <= PageCount ? null : $"1 ile {PageCount} arasında bir sayı girin.");
        if (v is not null) CurrentPage = int.Parse(v) - 1;
    }

    [RelayCommand]
    private async Task CopySelection()
    {
        if (Viewer is null) return;
        if (await Viewer.CopySelectionAsync()) StatusText = "Seçili metin panoya kopyalandı.";
        else if (!Viewer.HasSelection) StatusText = "Kopyalanacak metin seçilmedi.";
    }

    [RelayCommand] private async Task SelectAll() { if (Viewer is not null) await Viewer.SelectAllAsync(); }

    public void OnLinkActivated(LinkInfo link)
    {
        if (link.TargetPage >= 0) { CurrentPage = link.TargetPage; return; }
        if (link.Uri is null) return;
        if (!Uri.TryCreate(link.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            MessageDialog.Show("Bağlantı açılmadı", $"Bu bağlantı türü güvenlik nedeniyle açılmıyor:\n{link.Uri}", MessageKind.Warning);
            return;
        }
        if (_settings.ConfirmExternalLinks && !MessageDialog.Confirm("Dış bağlantı", $"Bu bağlantı varsayılan tarayıcınızda açılacak:\n\n{uri}", "Aç", "Vazgeç", MessageKind.Info))
            return;
        try { Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError("Bağlantıyı açma", ex); }
    }

    // ─────────────── Arama ───────────────

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _matchCase;
    [ObservableProperty] private bool _wholeWord;
    [ObservableProperty] private bool _ignoreDiacritics;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _searchStatus = "";
    [ObservableProperty] private IReadOnlyList<SearchHit>? _searchHits;
    [ObservableProperty] private SearchHit? _activeHit;
    [ObservableProperty] private SearchResultItem? _selectedResult;
    public ObservableCollection<SearchResultItem> SearchResults { get; } = new();

    partial void OnSelectedResultChanged(SearchResultItem? value) { if (value is not null) ActiveHit = value.Hit; }

    [RelayCommand]
    private async Task Search()
    {
        _searchCts?.Cancel();
        var q = SearchText.Trim();
        SearchResults.Clear();
        SearchHits = null; ActiveHit = null;
        if (q.Length == 0) { SearchStatus = ""; return; }
        LeftPanel = LeftPanelKind.Search;
        var cts = _searchCts = new CancellationTokenSource();
        var opts = new SearchOptions { MatchCase = MatchCase, WholeWord = WholeWord, IgnoreDiacritics = IgnoreDiacritics };
        IsSearching = true;
        int scanned = 0;
        var sw = Stopwatch.StartNew();
        var live = new List<SearchHit>();
        var progress = new Progress<(int Page, IReadOnlyList<SearchHit> Hits)>(p =>
        {
            if (cts.IsCancellationRequested) return;
            scanned++;
            live.AddRange(p.Hits);
            if (p.Hits.Count > 0 || scanned % 50 == 0) SearchStatus = $"{PageCount} sayfanın {scanned} tanesi tarandı · {live.Count} sonuç";
        });
        try
        {
            var hits = await Document.SearchAsync(q, opts, progress, cts.Token);
            if (cts.IsCancellationRequested) return;
            SearchHits = hits;
            foreach (var h in hits.Take(5000)) SearchResults.Add(new SearchResultItem { Hit = h });
            SearchStatus = hits.Count == 0 ? $"“{q}” bulunamadı." : $"{hits.Count} sonuç · {hits.Select(h => h.PageIndex).Distinct().Count()} sayfada · {sw.ElapsedMilliseconds} ms"
                + (hits.Count > 5000 ? " (ilk 5000 listelendi)" : "");
            var first = hits.FirstOrDefault(h => h.PageIndex >= CurrentPage) ?? hits.FirstOrDefault();
            if (first is not null) SelectedResult = SearchResults.FirstOrDefault(r => ReferenceEquals(r.Hit, first));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SearchStatus = "Arama başarısız: " + ex.Message; Log.Error("Arama", ex); }
        finally { if (ReferenceEquals(_searchCts, cts)) IsSearching = false; }
    }

    [RelayCommand]
    private void NextHit() => StepHit(+1);
    [RelayCommand]
    private void PreviousHit() => StepHit(-1);

    private void StepHit(int dir)
    {
        if (SearchResults.Count == 0) return;
        int i = SelectedResult is null ? -1 : SearchResults.IndexOf(SelectedResult);
        i = (i + dir + SearchResults.Count) % SearchResults.Count;
        SelectedResult = SearchResults[i];
    }

    [RelayCommand]
    private void ClearSearch()
    {
        _searchCts?.Cancel();
        SearchText = ""; SearchResults.Clear(); SearchHits = null; ActiveHit = null; SearchStatus = ""; IsSearching = false;
    }

    // ─────────────── Geri al / yinele ───────────────

    private async Task CheckpointAsync()
    {
        var snap = await Document.CreateSnapshotAsync(Path.Combine(AppPaths.Undo, Id.ToString("N")));
        _undo.Push(snap);
        foreach (var r in _redo) TryDelete(r.FilePath);
        _redo.Clear();
        if (_undo.Count > MaxUndo)
        {
            var all = _undo.Reverse().ToList();
            TryDelete(all[0].FilePath);
            _undo.Clear();
            foreach (var s in all.Skip(1)) _undo.Push(s);
        }
        CanUndo = true; CanRedo = false;
    }

    [RelayCommand]
    private Task Undo() => RunBusyAsync("Geri alınıyor…", async (_, _) =>
    {
        if (_undo.Count == 0) return;
        var current = await Document.CreateSnapshotAsync(Path.Combine(AppPaths.Undo, Id.ToString("N")));
        _redo.Push(current);
        var snap = _undo.Pop();
        await Document.RestoreSnapshotAsync(snap, modified: true);
        RefreshAfterChange();
        await LoadBookmarksAsync();
        StatusText = "Son işlem geri alındı.";
    });

    [RelayCommand]
    private Task Redo() => RunBusyAsync("Yineleniyor…", async (_, _) =>
    {
        if (_redo.Count == 0) return;
        var current = await Document.CreateSnapshotAsync(Path.Combine(AppPaths.Undo, Id.ToString("N")));
        _undo.Push(current);
        var snap = _redo.Pop();
        await Document.RestoreSnapshotAsync(snap, modified: true);
        RefreshAfterChange();
        await LoadBookmarksAsync();
        StatusText = "İşlem yinelendi.";
    });

    private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

    /// <summary>Belgeyi değiştiren bir işlemi geri alma noktasıyla yürütür.</summary>
    private Task<bool> EditAsync(string text, Func<Task> work, string status) => RunBusyAsync(text, async (_, _) =>
    {
        await CheckpointAsync();
        try { await work(); RefreshAfterChange(); }
        catch
        {
            // Başarısız işlem: geri alma noktasını geri yükle ki belge yarım kalmasın.
            if (_undo.TryPop(out var snap)) await Document.RestoreSnapshotAsync(snap, Document.IsModified);
            CanUndo = _undo.Count > 0;
            throw;
        }
        StatusText = status;
    });

    // ─────────────── Sayfa işlemleri ───────────────

    private IReadOnlyList<int> TargetPages() => SelectedPages.Count > 0 ? SelectedPages.OrderBy(i => i).ToList() : new[] { CurrentPage };

    [RelayCommand]
    private Task RotatePagesRight() => RotatePages(1);
    [RelayCommand]
    private Task RotatePagesLeft() => RotatePages(-1);

    private Task RotatePages(int turns)
    {
        var pages = TargetPages();
        return EditAsync("Sayfalar döndürülüyor…", () => Document.RotatePagesAsync(pages, turns), $"{pages.Count} sayfa döndürüldü.");
    }

    [RelayCommand]
    private async Task DeletePages()
    {
        var pages = TargetPages();
        if (pages.Count >= PageCount) { MessageDialog.Show("Sayfa silinemez", "Belgenin tüm sayfaları silinemez; en az bir sayfa kalmalıdır.", MessageKind.Warning); return; }
        if (!MessageDialog.Confirm("Sayfaları sil", pages.Count == 1 ? $"Sayfa {pages[0] + 1} silinsin mi?" : $"{pages.Count} sayfa ({PageRange.Format(pages)}) silinsin mi?", "Sil")) return;
        await DeletePagesAsync(pages);
    }

    /// <summary>Onay sormadan siler (onay komutta alınır; otomasyon da bunu kullanır).</summary>
    public async Task DeletePagesAsync(IReadOnlyList<int> pages)
    {
        await EditAsync("Sayfalar siliniyor…", () => Document.DeletePagesAsync(pages), $"{pages.Count} sayfa silindi. Geri almak için Ctrl+Z.");
        SelectedPages.Clear();
        await LoadBookmarksAsync();
    }

    [RelayCommand]
    private Task DuplicatePages()
    {
        var pages = TargetPages();
        return EditAsync("Sayfalar kopyalanıyor…", () => Document.DuplicatePagesAsync(pages), $"{pages.Count} sayfanın kopyası eklendi.");
    }

    public Task MovePagesAsync(IReadOnlyList<int> pages, int insertBefore)
        => EditAsync("Sayfalar taşınıyor…", () => Document.MovePagesAsync(pages, insertBefore), $"{pages.Count} sayfa taşındı.");

    [RelayCommand]
    private Task MovePagesUp()
    {
        var pages = TargetPages();
        if (pages[0] == 0) return Task.CompletedTask;
        return MovePagesAsync(pages, pages[0] - 1);
    }

    [RelayCommand]
    private Task MovePagesDown()
    {
        var pages = TargetPages();
        if (pages[^1] >= PageCount - 1) return Task.CompletedTask;
        return MovePagesAsync(pages, pages[^1] + 2);
    }

    [RelayCommand]
    private async Task InsertBlank()
    {
        var r = InsertBlankDialog.Ask(PageCount, CurrentPage, Document.GetPageSize(CurrentPage));
        if (r is null) return;
        await EditAsync("Boş sayfa ekleniyor…", () => Document.InsertBlankPagesAsync(r.Value.At, r.Value.Count, r.Value.Size), $"{r.Value.Count} boş sayfa eklendi.");
        CurrentPage = r.Value.At;
    }

    [RelayCommand]
    private async Task InsertFromFile()
    {
        var files = FileDialogs.OpenFiles(FileDialogs.OpenFilter, false, "Eklenecek PDF veya görüntüyü seçin");
        if (files.Length == 0) return;
        await InsertFilesAsync(files, null);
    }

    /// <summary>PDF/görüntü dosyalarını belirtilen konuma ekler (sürükle-bırak ve Dosyadan ekle).</summary>
    public async Task InsertFilesAsync(IReadOnlyList<string> files, int? at)
    {
        int position = at ?? CurrentPage + 1;
        if (at is null)
        {
            var asked = InsertPositionDialog.Ask(PageCount, CurrentPage, files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} dosya");
            if (asked is null) return;
            position = asked.Value;
        }
        int before = PageCount;
        await EditAsync("Sayfalar ekleniyor…", async () =>
        {
            int pos = position;
            foreach (var f in files)
            {
                if (FileDialogs.IsPdf(f))
                {
                    var src = await Main.OpenWithPasswordAsync(f);
                    if (src is null) continue;
                    using (src) { await Document.ImportPagesAsync(src, null, pos); pos += src.PageCount; }
                }
                else if (FileDialogs.IsImage(f))
                {
                    var images = await Task.Run(() => ImageCodec.LoadForPdf(f));
                    using var tmp = await PdfDocument.CreateFromImagesAsync(images, new ImagePageLayout(Document.GetPageSize(Math.Clamp(CurrentPage, 0, PageCount - 1)), 0, true));
                    await Document.ImportPagesAsync(tmp, null, pos);
                    pos += tmp.PageCount;
                }
            }
        }, "Sayfalar eklendi.");
        if (PageCount > before) CurrentPage = position;
    }

    [RelayCommand]
    private async Task ScanInsert()
    {
        var pages = ScanDialog.Ask(_settings, "Belgeye ekle");
        if (pages is null) return;
        var at = InsertPositionDialog.Ask(PageCount, CurrentPage, $"{pages.Count} taranmış sayfa");
        if (at is null) return;
        string note = "";
        await EditAsync("Taranan sayfalar ekleniyor…", async () =>
        {
            using var tmp = await ScanPdfBuilder.BuildAsync(pages);
            if (_settings.ScanOcr && MeslyPdfReader.Ocr.OcrEngine.Unavailable() is null)
            {
                BusyText = "Metin tanınıyor…";
                var opts = OcrRunOptions.From(_settings) with { Correction = ImageCorrection.None };
                note = " " + OcrWorkflow.Summarize(await OcrWorkflow.RecognizeAsync(tmp, Enumerable.Range(0, tmp.PageCount).ToList(), opts));
            }
            await Document.ImportPagesAsync(tmp, null, at.Value);
        }, $"{pages.Count} taranmış sayfa eklendi.");
        if (note.Length > 0) StatusText += note;
        CurrentPage = at.Value;
    }

    [RelayCommand]
    private async Task ExtractPages()
    {
        var r = PageRangeDialog.Ask("Sayfaları ayıkla", "Ayıklanacak sayfalar yeni bir PDF olarak kaydedilir.", PageRange.Format(TargetPages()), PageCount, "Ayıkla", allowSeparateFiles: true);
        if (r is null) return;
        if (r.Value.Separate)
        {
            var folder = FileDialogs.PickFolder("Sayfaların kaydedileceği klasör");
            if (folder is null) return;
            IReadOnlyList<string> files = Array.Empty<string>();
            await RunBusyAsync("Sayfalar ayıklanıyor…", async (p, ct) =>
                files = await PdfTools.SplitAsync(Document, r.Value.Pages.Select(i => (IReadOnlyList<int>)new[] { i }).ToList(), folder, Path.GetFileNameWithoutExtension(Title), p, ct),
                "extract", Title, () => folder);
            if (files.Count > 0) OfferOpenFolder($"{files.Count} dosya oluşturuldu.", folder);
            return;
        }
        var target = FileDialogs.SaveFile($"{Path.GetFileNameWithoutExtension(Title)} (s. {PageRange.Format(r.Value.Pages)}).pdf");
        if (target is null) return;
        await RunBusyAsync("Sayfalar ayıklanıyor…", async (_, ct) =>
        {
            using var part = await Document.ExtractAsync(r.Value.Pages);
            await part.SaveAsync(target, ct);
        }, "extract", Title, () => target);
        StatusText = $"{r.Value.Pages.Count} sayfa “{Path.GetFileName(target)}” dosyasına ayıklandı.";
    }

    [RelayCommand]
    private async Task CropPages()
    {
        var r = CropDialog.Ask(PageCount, PageRange.Format(TargetPages()), Document.GetPageSize(CurrentPage));
        if (r is null) return;
        await EditAsync("Sayfalar kırpılıyor…", () => Document.CropPagesAsync(r.Value.Pages, r.Value.Margins), $"{r.Value.Pages.Count} sayfa kırpıldı.");
    }

    [RelayCommand]
    private async Task ResizePages()
    {
        var r = PageSizeDialog.Ask(PageCount, PageRange.Format(Enumerable.Range(0, PageCount)), Document.GetPageSize(CurrentPage));
        if (r is null) return;
        await EditAsync("Sayfa boyutu değiştiriliyor…", () => Document.ResizePagesAsync(r.Value.Pages, r.Value.Size, r.Value.KeepOrientation),
            $"{r.Value.Pages.Count} sayfanın boyutu değiştirildi.");
    }

    [RelayCommand]
    private async Task Split()
    {
        var r = SplitDialog.Ask(PageCount, Path.GetFileNameWithoutExtension(Title), Document.FilePath is { } p ? Path.GetDirectoryName(p) : null);
        if (r is null) return;
        IReadOnlyList<string> files = Array.Empty<string>();
        await RunBusyAsync("Belge bölünüyor…", async (p, ct) =>
        {
            var groups = PdfTools.PlanSplit(PageCount, r.Plan);
            files = await PdfTools.SplitAsync(Document, groups, r.Folder, r.BaseName, p, ct);
        }, "split", Title, () => r.Folder);
        if (files.Count > 0) OfferOpenFolder($"Belge {files.Count} dosyaya bölündü.", r.Folder);
    }

    // ─────────────── Yer imleri ───────────────

    [ObservableProperty] private BookmarkNode? _selectedBookmark;

    partial void OnSelectedBookmarkChanged(BookmarkNode? value)
    {
        if (value is not null && value.PageIndex >= 0) CurrentPage = value.PageIndex;
    }

    public bool CanEditBookmarks => Document.CanEditBookmarks;

    private async Task CommitBookmarksAsync(string status)
    {
        if (!await EditAsync("Yer imleri güncelleniyor…", () => Document.SetBookmarksAsync(Bookmarks.Select(b => b.ToModel())), status))
            await LoadBookmarksAsync(); // başarısız olursa listeyi belgedeki hale döndür
    }

    private bool EnsureBookmarksEditable()
    {
        if (Document.CanEditBookmarks) return true;
        MessageDialog.Show("Yer imi düzenlenemiyor", PdfDocument.EncryptedBookmarkMessage, MessageKind.Warning);
        return false;
    }

    [RelayCommand]
    private async Task AddBookmark()
    {
        if (!EnsureBookmarksEditable()) return;
        string suggestion = $"Sayfa {CurrentPage + 1}";
        if (Viewer?.HasSelection == true)
        {
            var sel = (await Viewer.GetSelectedTextAsync()).Replace("\r\n", " ").Trim();
            if (sel.Length is > 0 and <= 120) suggestion = sel;
        }
        var title = InputDialog.Ask("Yer imi ekle", $"Sayfa {CurrentPage + 1} için yer imi adı", suggestion,
            validate: s => string.IsNullOrWhiteSpace(s) ? "Yer imi adı boş olamaz." : null);
        if (title is null) return;
        var node = new BookmarkNode { Title = title.Trim(), PageIndex = CurrentPage };
        var parent = SelectedBookmark?.Parent;
        var list = parent?.Children ?? Bookmarks;
        int idx = SelectedBookmark is null ? list.Count : list.IndexOf(SelectedBookmark) + 1;
        node.Parent = parent;
        list.Insert(Math.Clamp(idx, 0, list.Count), node);
        LeftPanel = LeftPanelKind.Bookmarks;
        await CommitBookmarksAsync("Yer imi eklendi. Kaydettiğinizde PDF'e yazılır.");
    }

    [RelayCommand]
    private async Task AddChildBookmark()
    {
        if (!EnsureBookmarksEditable()) return;
        if (SelectedBookmark is not { } parent) { await AddBookmark(); return; }
        var title = InputDialog.Ask("Alt yer imi ekle", $"“{parent.Title}” altında, sayfa {CurrentPage + 1}", $"Sayfa {CurrentPage + 1}",
            validate: s => string.IsNullOrWhiteSpace(s) ? "Yer imi adı boş olamaz." : null);
        if (title is null) return;
        parent.Children.Add(new BookmarkNode { Title = title.Trim(), PageIndex = CurrentPage, Parent = parent });
        parent.IsExpanded = true;
        await CommitBookmarksAsync("Alt yer imi eklendi.");
    }

    [RelayCommand]
    private async Task RenameBookmark()
    {
        if (!EnsureBookmarksEditable()) return;
        if (SelectedBookmark is not { } b) return;
        var title = InputDialog.Ask("Yer imini yeniden adlandır", "Yeni ad", b.Title, validate: s => string.IsNullOrWhiteSpace(s) ? "Yer imi adı boş olamaz." : null);
        if (title is null || title == b.Title) return;
        b.Title = title.Trim();
        await CommitBookmarksAsync("Yer imi yeniden adlandırıldı.");
    }

    [RelayCommand]
    private async Task SetBookmarkToCurrentPage()
    {
        if (!EnsureBookmarksEditable()) return;
        if (SelectedBookmark is not { } b) return;
        b.PageIndex = CurrentPage; b.Uri = null;
        await CommitBookmarksAsync($"Yer imi sayfa {CurrentPage + 1}'e yönlendirildi.");
    }

    [RelayCommand]
    private async Task DeleteBookmark()
    {
        if (!EnsureBookmarksEditable()) return;
        if (SelectedBookmark is not { } b) return;
        if (b.Children.Count > 0 && !MessageDialog.Confirm("Yer imini sil", $"“{b.Title}” ve {Bookmark.Count(b.ToModel().Children)} alt yer imi silinsin mi?", "Sil")) return;
        (b.Parent?.Children ?? Bookmarks).Remove(b);
        SelectedBookmark = null;
        await CommitBookmarksAsync("Yer imi silindi.");
    }

    [RelayCommand]
    private async Task MoveBookmark(string direction)
    {
        if (!EnsureBookmarksEditable()) return;
        if (SelectedBookmark is not { } b) return;
        var list = b.Parent?.Children ?? Bookmarks;
        int i = list.IndexOf(b), j = direction == "up" ? i - 1 : i + 1;
        if (j < 0 || j >= list.Count) return;
        list.Move(i, j);
        await CommitBookmarksAsync("Yer imi taşındı.");
    }

    // ─────────────── Kaydetme ───────────────

    [RelayCommand]
    public async Task<bool> Save()
    {
        if (Document.FilePath is null) return await SaveAs();
        return await SaveToAsync(Document.FilePath);
    }

    [RelayCommand]
    public async Task<bool> SaveAs()
    {
        var target = FileDialogs.SaveFile(Document.FilePath is null ? Title : Path.GetFileName(Document.FilePath),
            initialDir: Document.FilePath is null ? null : Path.GetDirectoryName(Document.FilePath));
        if (target is null) return false;
        return await SaveToAsync(target);
    }

    private async Task<bool> SaveToAsync(string path)
    {
        var ok = await RunBusyAsync("Kaydediliyor…", async (_, ct) => await Document.SaveAsync(path, ct), "save", path, () => path);
        if (ok)
        {
            StatusText = $"Kaydedildi: {Path.GetFileName(path)}";
            Main.RecordRecent(this);
            Main.Recovery.Forget(this);
            await LoadInfoAsync();
        }
        return ok;
    }

    // ─────────────── Dışa aktarma ───────────────

    [RelayCommand]
    private async Task ExportImages()
    {
        var r = ExportImagesDialog.Ask(PageCount, PageRange.Format(SelectedPages.Count > 1 ? SelectedPages : Enumerable.Range(0, PageCount)), _settings.ExportDpi, _settings.JpegQuality,
            Document.FilePath is { } p ? Path.GetDirectoryName(p) : null);
        if (r is null) return;
        _settings.ExportDpi = r.Dpi; _settings.JpegQuality = r.Quality;
        int written = 0;
        string baseName = Path.GetFileNameWithoutExtension(Title);
        await RunBusyAsync("Sayfalar görüntü olarak dışa aktarılıyor…", async (progress, ct) =>
        {
            if (r.Format == ImageFormatKind.Tiff && r.SingleTiff)
            {
                var path = PdfTools.UniquePath(Path.Combine(r.Folder, baseName + ".tif"));
                var pages = new List<BgraImage>();
                for (int k = 0; k < r.Pages.Count; k++)
                {
                    pages.Add(await PdfTools.RenderAtDpiAsync(Document, r.Pages[k], r.Dpi, ct));
                    progress.Report((k + 1) / (double)r.Pages.Count * 0.9);
                }
                await Task.Run(() => ImageCodec.SaveMultiPageTiff(pages, path, r.Dpi), ct);
                written = 1;
                return;
            }
            var ext = r.Format switch { ImageFormatKind.Jpeg => ".jpg", ImageFormatKind.Tiff => ".tif", ImageFormatKind.Bmp => ".bmp", _ => ".png" };
            int digits = Math.Max(3, PageCount.ToString().Length);
            for (int k = 0; k < r.Pages.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                int page = r.Pages[k];
                var img = await PdfTools.RenderAtDpiAsync(Document, page, r.Dpi, ct);
                var path = Path.Combine(r.Folder, $"{baseName}_s{(page + 1).ToString().PadLeft(digits, '0')}{ext}");
                await Task.Run(() => ImageCodec.Save(img, path, r.Format, r.Dpi, r.Quality), ct);
                written++;
                progress.Report((k + 1) / (double)r.Pages.Count);
            }
        }, "export-images", Title, () => r.Folder);
        if (written > 0) OfferOpenFolder($"{written} görüntü dosyası oluşturuldu.", r.Folder);
    }

    [RelayCommand]
    private async Task ExportText()
    {
        var target = FileDialogs.SaveFile(Path.GetFileNameWithoutExtension(Title) + ".txt", "Metin dosyası (*.txt)|*.txt", Document.FilePath is { } p ? Path.GetDirectoryName(p) : null, "Metin olarak kaydet");
        if (target is null) return;
        bool empty = false;
        var ok = await RunBusyAsync("Metin dışa aktarılıyor…", async (progress, ct) =>
        {
            await PdfTools.ExportTextAsync(Document, target, null, progress, ct);
            empty = new FileInfo(target).Length <= 3 + PageCount * 4;
        }, "export-text", Title, () => target);
        if (!ok) return;
        if (empty) MessageDialog.Show("Metin bulunamadı", "Belgede seçilebilir metin yok; taranmış bir belge olabilir. Önce Belge sekmesindeki “Metni tanı” ile metni tanıyın, sonra yeniden dışa aktarın.", MessageKind.Warning);
        else StatusText = $"Metin kaydedildi: {Path.GetFileName(target)}";
    }

    [RelayCommand]
    private void Print() => PrintPreviewWindow.ShowFor(this);

    private static void OfferOpenFolder(string message, string folder)
    {
        if (MessageDialog.Confirm("İşlem tamamlandı", message + "\n\nKlasör açılsın mı?", "Klasörü aç", "Kapat", MessageKind.Success))
            try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _searchCts?.Cancel();
        SaveState();
        Document.Changed -= OnDocumentChanged;
        Document.Dispose();
        try { Directory.Delete(Path.Combine(AppPaths.Undo, Id.ToString("N")), true); } catch { }
    }
}
