using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Core;
using MeslyPdfReader.Data;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.ViewModels;

public enum RibbonTab { File, Home, Pages, Convert, View }

public sealed partial class MainViewModel : BusyViewModel
{
    private readonly AppSettings _settings;
    private readonly AppDatabase _db;
    public ThemeService Theme { get; }
    public RecoveryService Recovery { get; }

    public MainViewModel(AppSettings settings, AppDatabase db, ThemeService theme, RecoveryService recovery)
    {
        _settings = settings; _db = db; Db = db; Theme = theme; Recovery = recovery;
        Recovery.Start(() => Tabs);
        LoadRecent();
    }

    public ObservableCollection<DocumentViewModel> Tabs { get; } = new();
    public ObservableCollection<RecentItem> Recent { get; } = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsHome), nameof(HasDocument))] private DocumentViewModel? _activeTab;
    [ObservableProperty] private RibbonTab _ribbon = RibbonTab.Home;
    [ObservableProperty] private bool _isFullScreen;

    public bool IsHome => ActiveTab is null;
    public bool HasDocument => ActiveTab is not null;
    public string AppVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    partial void OnActiveTabChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
        DocumentViewModel? value = newValue;
        if (value is null && Ribbon is RibbonTab.Pages or RibbonTab.View) Ribbon = RibbonTab.Home;
    }

    [RelayCommand] private void ShowHome() => ActiveTab = null;
    [RelayCommand] private void SelectRibbon(RibbonTab tab) => Ribbon = tab;

    // ─────────────── Son dosyalar ───────────────

    public void LoadRecent()
    {
        Recent.Clear();
        if (!_settings.KeepHistory) return;
        try { foreach (var r in _db.GetRecent(_settings.RecentLimit)) Recent.Add(new RecentItem { File = r }); }
        catch (Exception ex) { Log.Warn("Son dosyalar okunamadı: " + ex.Message); }
    }

    public void RecordRecent(DocumentViewModel vm)
    {
        if (!_settings.KeepHistory || vm.Document.FilePath is null) return;
        try { _db.AddRecent(vm.Document.FilePath, vm.PageCount, new FileInfo(vm.Document.FilePath).Length); LoadRecent(); }
        catch (Exception ex) { Log.Warn("Son dosya kaydedilemedi: " + ex.Message); }
    }

    [RelayCommand]
    private async Task OpenRecent(RecentItem? item)
    {
        if (item is null) return;
        if (!item.Exists)
        {
            if (MessageDialog.Confirm("Dosya bulunamadı", $"“{item.Name}” taşınmış veya silinmiş olabilir.\n{item.File.Path}\n\nListeden kaldırılsın mı?", "Kaldır", "Vazgeç"))
            { _db.RemoveRecent(item.File.Path); LoadRecent(); }
            return;
        }
        await OpenFilesAsync(new[] { item.File.Path });
    }

    [RelayCommand]
    private void TogglePin(RecentItem? item)
    {
        if (item is null) return;
        _db.SetPinned(item.File.Path, !item.Pinned);
        LoadRecent();
    }

    [RelayCommand]
    private void RemoveRecent(RecentItem? item)
    {
        if (item is null) return;
        _db.RemoveRecent(item.File.Path);
        LoadRecent();
    }

    [RelayCommand]
    private void OpenContainingFolder(RecentItem? item)
    {
        if (item is null || !item.Exists) return;
        try { Process.Start("explorer.exe", $"/select,\"{item.File.Path}\""); } catch { }
    }

    [RelayCommand]
    private void ClearRecent()
    {
        if (!MessageDialog.Confirm("Geçmişi temizle", "Sabitlenmemiş tüm son dosyalar listeden kaldırılsın mı? (Dosyalar silinmez.)", "Temizle")) return;
        _db.ClearRecent();
        LoadRecent();
    }

    // ─────────────── Açma ───────────────

    [RelayCommand]
    private async Task Open()
    {
        var files = FileDialogs.OpenFiles();
        if (files.Length > 0) await OpenFilesAsync(files);
    }

    /// <summary>PDF'leri sekmede açar; görüntüler için görsellerden PDF akışını başlatır.</summary>
    public async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var images = list.Where(FileDialogs.IsImage).ToList();
        foreach (var path in list.Where(FileDialogs.IsPdf))
        {
            var full = Path.GetFullPath(path);
            var existing = Tabs.FirstOrDefault(t => t.Document.FilePath is { } fp && string.Equals(fp, full, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) { ActiveTab = existing; continue; }
            PdfDocument? doc = null;
            await RunBusyAsync($"{Path.GetFileName(path)} açılıyor…", async (_, _) => doc = await OpenWithPasswordAsync(full));
            if (doc is null) continue;
            AddTab(doc);
        }
        var unsupported = list.Where(p => !FileDialogs.IsPdf(p) && !FileDialogs.IsImage(p)).ToList();
        if (unsupported.Count > 0)
            MessageDialog.Show("Desteklenmeyen dosya", "Şu dosyalar açılamadı (yalnızca PDF ve JPG/PNG/TIFF/BMP desteklenir):\n" + string.Join("\n", unsupported.Select(Path.GetFileName)), MessageKind.Warning);
        if (images.Count > 0) await CreateFromImagesAsync(images);
    }

    /// <summary>Gerekirse parola sorarak açar. Kullanıcı vazgeçerse null.</summary>
    public async Task<PdfDocument?> OpenWithPasswordAsync(string path)
    {
        string? password = null;
        bool wrong = false;
        while (true)
        {
            try { return await PdfDocument.OpenAsync(path, password); }
            catch (PdfException ex) when (ex.Kind == PdfErrorKind.Password)
            {
                password = Application.Current.Dispatcher.Invoke(() => PasswordDialog.Ask(Path.GetFileName(path), wrong));
                if (password is null) return null;
                wrong = true;
            }
        }
    }

    public DocumentViewModel AddTab(PdfDocument doc, bool record = true)
    {
        var vm = new DocumentViewModel(this, doc, _settings, _db);
        Tabs.Add(vm);
        ActiveTab = vm;
        if (record) RecordRecent(vm);
        return vm;
    }

    [RelayCommand]
    public async Task<bool> CloseTab(DocumentViewModel? vm)
    {
        vm ??= ActiveTab;
        if (vm is null) return true;
        if (vm.Document.IsModified)
        {
            ActiveTab = vm;
            switch (MessageDialog.AskSave(vm.Title))
            {
                case SaveChoice.Cancel: return false;
                case SaveChoice.Save: if (!await vm.Save()) return false; break;
            }
        }
        int idx = Tabs.IndexOf(vm);
        Tabs.Remove(vm);
        Recovery.Forget(vm);
        vm.Dispose();
        if (ReferenceEquals(ActiveTab, vm) || ActiveTab is null)
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(idx, 0, Tabs.Count - 1)];
        return true;
    }

    [RelayCommand]
    private async Task CloseAllTabs()
    {
        foreach (var t in Tabs.ToList()) if (!await CloseTab(t)) return;
    }

    /// <summary>Uygulama kapanmadan önce: kaydedilmemiş belgeleri sor. false → kapatma iptal.</summary>
    public async Task<bool> PrepareExitAsync()
    {
        foreach (var t in Tabs.ToList())
        {
            if (!t.Document.IsModified) continue;
            ActiveTab = t;
            switch (MessageDialog.AskSave(t.Title))
            {
                case SaveChoice.Cancel: return false;
                case SaveChoice.Save: if (!await t.Save()) return false; break;
            }
        }
        foreach (var t in Tabs.ToList()) { Recovery.Forget(t); t.Dispose(); }
        Tabs.Clear();
        return true;
    }

    // ─────────────── Oluşturma / dönüştürme ───────────────

    [RelayCommand]
    private async Task CreateBlank()
    {
        var r = InsertBlankDialog.Ask(0, 0, PaperSize.A4.ToPoints(), forNewDocument: true);
        if (r is null) return;
        var doc = await PdfDocument.CreateAsync(r.Value.Size, r.Value.Count);
        AddTab(doc, record: false);
    }

    [RelayCommand]
    private async Task ScanToPdf()
    {
        var pages = ScanDialog.Ask(_settings, "PDF oluştur");
        if (pages is null) return;
        PdfDocument? doc = null;
        var ok = await RunBusyAsync("Taranan sayfalar PDF'e dönüştürülüyor…", async (p, ct) => doc = await ScanPdfBuilder.BuildAsync(pages, p, ct),
            "scan", $"{pages.Count} sayfa");
        if (ok && doc is not null)
        {
            var vm = AddTab(doc, record: false);
            vm.StatusText = $"{pages.Count} taranmış sayfa. Kaydetmek için Ctrl+S.";
        }
    }

    [RelayCommand]
    private async Task CreateFromImages()
    {
        var files = FileDialogs.OpenFiles(FileDialogs.ImageFilter, true, "PDF'e dönüştürülecek görüntüleri seçin");
        if (files.Length > 0) await CreateFromImagesAsync(files);
    }

    public async Task CreateFromImagesAsync(IReadOnlyList<string> files)
    {
        var r = ImagesToPdfDialog.Ask(files);
        if (r is null) return;
        PdfDocument? doc = null;
        var ok = await RunBusyAsync("Görüntüler PDF'e dönüştürülüyor…", async (progress, ct) =>
        {
            var sources = new List<ImagePageSource>();
            for (int i = 0; i < r.Files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = r.Files[i];
                try { sources.AddRange(await Task.Run(() => ImageCodec.LoadForPdf(f), ct)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { throw new PdfException(PdfErrorKind.Format, $"“{Path.GetFileName(f)}” görüntüsü okunamadı: {ex.Message}", ex); }
                progress.Report((i + 1) / (double)r.Files.Count * 0.5);
            }
            doc = await PdfDocument.CreateFromImagesAsync(sources, r.Layout, new Progress<double>(p => progress.Report(0.5 + p * 0.5)), ct);
        }, "images-to-pdf", string.Join("; ", r.Files.Select(Path.GetFileName)));
        if (!ok || doc is null) return;
        if (r.SavePath is not null)
        {
            var saved = await RunBusyAsync("Kaydediliyor…", async (_, ct) => await doc.SaveAsync(r.SavePath, ct));
            if (!saved) { AddTab(doc, record: false); return; }
        }
        AddTab(doc, record: r.SavePath is not null);
    }

    [RelayCommand]
    private async Task Merge()
    {
        var initial = ActiveTab?.Document.FilePath is { } p && !ActiveTab.Document.IsModified ? new[] { p } : Array.Empty<string>();
        var r = MergeDialog.Ask(initial);
        if (r is null) return;
        PdfDocument? merged = null;
        var ok = await RunBusyAsync("Belgeler birleştiriliyor…", async (progress, ct) =>
        {
            // Parola gerekenleri önceden sor.
            var sources = new List<MergeSource>();
            foreach (var item in r.Items)
            {
                string? pw = null;
                using (var probe = await OpenWithPasswordAsyncCapture(item.Path, v => pw = v))
                    if (probe is null) throw new OperationCanceledException();
                sources.Add(new MergeSource(item.Path, pw, item.Range));
            }
            merged = await PdfTools.MergeAsync(sources, progress, ct);
            if (r.SavePath is not null) await merged.SaveAsync(r.SavePath, ct);
        }, "merge", string.Join("; ", r.Items.Select(i => Path.GetFileName(i.Path))), () => r.SavePath);
        if (!ok || merged is null) { merged?.Dispose(); return; }
        if (r.OpenResult || r.SavePath is null) AddTab(merged, record: r.SavePath is not null);
        else
        {
            merged.Dispose();
            StatusText = $"Birleştirilen belge kaydedildi: {Path.GetFileName(r.SavePath)}";
            if (MessageDialog.Confirm("Birleştirme tamamlandı", $"{r.Items.Count} belge birleştirildi:\n{r.SavePath}\n\nAçılsın mı?", "Aç", "Kapat", MessageKind.Success))
                await OpenFilesAsync(new[] { r.SavePath });
        }
    }

    private async Task<PdfDocument?> OpenWithPasswordAsyncCapture(string path, Action<string?> setPassword)
    {
        string? password = null; bool wrong = false;
        while (true)
        {
            try { var d = await PdfDocument.OpenAsync(path, password); setPassword(password); return d; }
            catch (PdfException ex) when (ex.Kind == PdfErrorKind.Password)
            {
                password = Application.Current.Dispatcher.Invoke(() => PasswordDialog.Ask(Path.GetFileName(path), wrong));
                if (password is null) return null;
                wrong = true;
            }
        }
    }

    /// <summary>Ana ekrandan bir PDF seçip ilgili işlemi açar (dönüştür, böl).</summary>
    [RelayCommand]
    private async Task ConvertPdf()
    {
        var vm = ActiveTab ?? await PickAndOpenAsync("Dönüştürülecek PDF'yi seçin");
        if (vm is null) return;
        var choice = ConvertChoiceDialog.Ask(vm.Title);
        if (choice == "images") vm.ExportImagesCommand.Execute(null);
        else if (choice == "text") vm.ExportTextCommand.Execute(null);
    }

    [RelayCommand]
    private async Task SplitPdf()
    {
        var vm = ActiveTab ?? await PickAndOpenAsync("Bölünecek PDF'yi seçin");
        vm?.SplitCommand.Execute(null);
    }

    private async Task<DocumentViewModel?> PickAndOpenAsync(string title)
    {
        var f = FileDialogs.OpenFiles(FileDialogs.PdfFilter, false, title);
        if (f.Length == 0) return null;
        await OpenFilesAsync(f);
        return ActiveTab;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        SettingsWindow.ShowFor(this, _settings, _db);
        LoadRecent();
        Recovery.Refresh();
    }

    /// <summary>Beklenmedik kapanmadan kalan kurtarma kopyalarını önerir.</summary>
    public async Task OfferRecoveryAsync()
    {
        var left = RecoveryService.FindLeftovers();
        if (left.Count == 0) return;
        var chosen = RecoveryDialog.Ask(left);
        foreach (var e in left)
        {
            if (chosen is not null && chosen.Contains(e))
            {
                try
                {
                    // Kurtarma kopyası geçici klasöre taşınır ve özgün dosya yolu kaydetme hedefi olur.
                    var tmp = Path.Combine(AppPaths.Temp, $"kurtarilan-{e.Id}.pdf");
                    File.Copy(e.RecoveryFile, tmp, true);
                    var doc = await PdfDocument.OpenAsync(tmp);
                    doc.SetSaveTarget(e.OriginalPath, markModified: true);
                    var vm = AddTab(doc, record: false);
                    vm.StatusText = "Kurtarılan belge — kaydetmeyi unutmayın.";
                }
                catch (Exception ex) { ShowError("Belge kurtarma", ex); }
            }
            RecoveryService.Discard(e);
        }
    }
}
