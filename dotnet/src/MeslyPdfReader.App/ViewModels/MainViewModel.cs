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
using MeslyPdfReader.Ocr;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.ViewModels;

/// <summary>FineReader PDF düzenindeki sekmeler: Belge, Sayfaları Düzenle, İçeriği Düzenle.</summary>
public enum HomeSection { Open, Scan, Recent }

public enum RibbonTab { Document, Organize, EditContent }

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
    [ObservableProperty] private RibbonTab _ribbon = RibbonTab.Document;
    [ObservableProperty] private bool _isFullScreen;

    public bool IsHome => ActiveTab is null;
    public bool HasDocument => ActiveTab is not null;
    public string AppVersion => typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    partial void OnActiveTabChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
        DocumentViewModel? value = newValue;
        // Yeni bir belgeye geçilince Belge sekmesinden başlanır; İçeriği Düzenle açıkken geçilirse o belgede de düzenleme açılır.
        if (value is not null && Ribbon == RibbonTab.EditContent) value.Tool = ViewerTool.Edit;
        if (oldValue is not null && oldValue.Tool == ViewerTool.Edit && Ribbon == RibbonTab.EditContent) oldValue.Tool = ViewerTool.Select;
        if (value is not null) value.IsOrganizeMode = Ribbon == RibbonTab.Organize;
        if (oldValue is not null) oldValue.IsOrganizeMode = false;
    }

    /// <summary>FineReader düzeni: “Düzenle” sekmesi düzenleme kipidir; sekmeye girince araç açılır, çıkınca kapanır.</summary>
    partial void OnRibbonChanged(RibbonTab oldValue, RibbonTab newValue)
    {
        if (ActiveTab is not { } t) return;
        if (newValue == RibbonTab.EditContent) t.Tool = ViewerTool.Edit;
        else if (oldValue == RibbonTab.EditContent && t.Tool == ViewerTool.Edit) t.Tool = ViewerTool.Select;
        t.IsOrganizeMode = newValue == RibbonTab.Organize;
    }

    [RelayCommand] private void ShowHome() => ActiveTab = null;

    /// <summary>Yeni Görev ekranındaki bölüm (Aç, Tara, Son Kullanılanlar).</summary>
    [ObservableProperty] private HomeSection _homeSection = HomeSection.Open;

    private static readonly string[] OcrLangCodes = { "tur+eng", "tur", "eng" };
    /// <summary>Yeni Görev alt çubuğundaki belge dilleri.</summary>
    public int OcrLanguageIndex
    {
        get => Math.Max(0, Array.IndexOf(OcrLangCodes, _settings.OcrLanguages));
        set { if (value >= 0 && value < OcrLangCodes.Length) { _settings.OcrLanguages = OcrLangCodes[value]; OnPropertyChanged(); } }
    }
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
        var office = list.Where(OfficeExport.IsOfficeFile).ToList();
        var unsupported = list.Where(p => !FileDialogs.IsPdf(p) && !FileDialogs.IsImage(p) && !OfficeExport.IsOfficeFile(p)).ToList();
        if (unsupported.Count > 0)
            MessageDialog.Show("Desteklenmeyen dosya", "Şu dosyalar açılamadı (PDF, JPG/PNG/TIFF/BMP ve Office belgeleri desteklenir):\n" + string.Join("\n", unsupported.Select(Path.GetFileName)), MessageKind.Warning);
        if (images.Count > 0) await CreateFromImagesAsync(images);
        if (office.Count > 0) await ConvertOfficeFilesAsync(office);
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
        string ocrNote = "";
        var ok = await RunBusyAsync("Taranan sayfalar PDF'e dönüştürülüyor…", async (p, ct) =>
        {
            doc = await ScanPdfBuilder.BuildAsync(pages, null, ct);
            if (_settings.ScanOcr && OcrEngine.Unavailable() is null)
            {
                BusyText = "Metin tanınıyor…";
                // Görüntü düzeltmesi tarama penceresinde yapıldı; burada yalnız tanıma.
                var opts = OcrRunOptions.From(_settings) with { Correction = ImageCorrection.None };
                var r = await OcrWorkflow.RecognizeAsync(doc, Enumerable.Range(0, doc.PageCount).ToList(), opts, p, ct);
                ocrNote = " " + OcrWorkflow.Summarize(r);
            }
            else if (_settings.ScanOcr) ocrNote = " Metin tanınamadı: " + OcrEngine.Unavailable();
        }, "scan", $"{pages.Count} sayfa");
        if (ok && doc is not null)
        {
            var vm = AddTab(doc, record: false);
            vm.StatusText = $"{pages.Count} taranmış sayfa.{ocrNote} Kaydetmek için Ctrl+S.";
        }
        else doc?.Dispose();
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
        if (choice == "word") await vm.ConvertAsync(ConvertFormat.Word);
        else if (choice == "excel") await vm.ConvertAsync(ConvertFormat.Excel);
        else if (choice == "images") vm.ExportImagesCommand.Execute(null);
        else if (choice == "text") vm.ExportTextCommand.Execute(null);
    }

    [RelayCommand]
    private async Task SplitPdf()
    {
        var vm = ActiveTab ?? await PickAndOpenAsync("Bölünecek PDF'yi seçin");
        vm?.SplitCommand.Execute(null);
    }

    /// <summary>Ana ekran: PDF seçip doğrudan Düzenle kipinde açar.</summary>
    [RelayCommand]
    private async Task EditPdf()
    {
        var vm = await PickAndOpenAsync("Düzenlenecek PDF'yi seçin");
        if (vm is null) return;
        Ribbon = RibbonTab.EditContent;
        vm.Tool = ViewerTool.Edit;
    }

    /// <summary>Ana ekran: taranmış PDF'yi seçip metin tanımayı başlatır.</summary>
    [RelayCommand]
    private async Task OcrPdf()
    {
        var vm = await PickAndOpenAsync("Metni tanınacak (taranmış) PDF'yi seçin");
        if (vm is null) return;
        Ribbon = RibbonTab.Document;
        await vm.RunOcrAsync(ocr: true, pages: null);
    }

    // ─────────────── Aşama 5: dönüştürme ───────────────

    [RelayCommand]
    private async Task OfficeToPdf()
    {
        var files = FileDialogs.OpenFiles(OfficeExport.Filter, true, "PDF'ye dönüştürülecek Office belgelerini seçin");
        if (files.Length > 0) await ConvertOfficeFilesAsync(files);
    }

    /// <summary>
    /// Office belgelerini PDF'ye çevirip açar. Tek dosyada hedef sorulur; birden çoksa PDF'ler özgün dosyaların yanına
    /// (ad çakışırsa “(2)” eklenerek) kaydedilir. Kurulu Office/LibreOffice yoksa açıklayıcı ileti gösterilir.
    /// </summary>
    public async Task<IReadOnlyList<string>> ConvertOfficeFilesAsync(IReadOnlyList<string> files, IReadOnlyList<string>? targets = null)
    {
        var done = new List<string>();
        if (files.Count == 0) return done;
        if (files.All(f => OfficeExport.EngineFor(f) == OfficeEngine.None))
        {
            MessageDialog.Show("Office veya LibreOffice bulunamadı", OfficeExport.NoEngineMessage, MessageKind.Error);
            return done;
        }
        var outs = targets?.ToList();
        if (outs is null)
        {
            if (files.Count == 1)
            {
                var t = FileDialogs.SaveFile(Path.GetFileNameWithoutExtension(files[0]) + ".pdf", FileDialogs.PdfFilter, Path.GetDirectoryName(files[0]), "PDF olarak kaydet");
                if (t is null) return done;
                outs = new() { t };
            }
            else
            {
                if (DialogBase.Automation is null && !MessageDialog.Confirm("Office belgelerini PDF'ye dönüştür",
                    $"{files.Count} dosya PDF'ye dönüştürülecek. PDF'ler özgün dosyaların bulunduğu klasörlere kaydedilir; özgün dosyalar değişmez.", "Dönüştür", "İptal", MessageKind.Info))
                    return done;
                outs = files.Select(f => PdfTools.UniquePath(Path.ChangeExtension(f, ".pdf"))).ToList();
            }
        }
        var errors = new List<string>();
        await RunBusyAsync(files.Count == 1 ? $"{Path.GetFileName(files[0])} PDF'ye dönüştürülüyor…" : $"{files.Count} Office belgesi PDF'ye dönüştürülüyor…", async (progress, ct) =>
        {
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await OfficeExport.ConvertAsync(files[i], outs[i], ct);
                    done.Add(outs[i]);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Error("Office → PDF: " + files[i], ex);
                    errors.Add($"{Path.GetFileName(files[i])}: {ex.Message}");
                }
                progress.Report((i + 1.0) / files.Count);
            }
        }, "office-pdf", string.Join("; ", files.Select(Path.GetFileName)), () => string.Join("; ", done));
        if (errors.Count > 0)
            MessageDialog.Show(done.Count == 0 ? "Dönüştürülemedi" : "Bazı dosyalar dönüştürülemedi", string.Join("\n\n", errors), MessageKind.Error);
        if (done.Count > 0)
        {
            StatusText = done.Count == 1 ? $"PDF oluşturuldu: {Path.GetFileName(done[0])}" : $"{done.Count} PDF oluşturuldu.";
            await OpenFilesAsync(done.Take(10));
        }
        return done;
    }

    [RelayCommand]
    private async Task ConvertToWordHome()
    {
        var vm = ActiveTab ?? await PickAndOpenAsync("Word'e dönüştürülecek PDF'yi seçin");
        if (vm is not null) await vm.ConvertAsync(ConvertFormat.Word);
    }

    [RelayCommand]
    private async Task ConvertToExcelHome()
    {
        var vm = ActiveTab ?? await PickAndOpenAsync("Excel'e dönüştürülecek PDF'yi seçin");
        if (vm is not null) await vm.ConvertAsync(ConvertFormat.Excel);
    }

    [RelayCommand]
    private async Task CompressHome()
    {
        var vm = ActiveTab ?? await PickAndOpenAsync("Sıkıştırılacak PDF'yi seçin");
        if (vm is not null) await vm.CompressAsync();
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
