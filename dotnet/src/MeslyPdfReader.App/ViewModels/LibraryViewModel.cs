using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Data;

namespace MeslyPdfReader.App.ViewModels;

public enum LibraryTab { Recent, All, Favorites }

/// <summary>Belgelerim listesindeki bir satır.</summary>
public sealed partial class LibraryItem : ObservableObject
{
    public required string Path { get; init; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public DateTime Modified { get; init; }
    public long Size { get; init; }
    public int PageCount { get; init; }
    public bool Exists { get; init; }
    [ObservableProperty] private bool _pinned;
    public string ModifiedText => Exists ? Modified.ToString("dd.MM.yyyy HH:mm") : "bulunamadı";
    public string SizeText => Exists ? RecentItem.FormatSize(Size) : "—";
}

/// <summary>
/// Afiş 2 — “Belgelerim”: Son Kullanılanlar, Tüm Belgeler (geçmiş + Belgeler / Masaüstü / İndirilenler klasörlerindeki
/// PDF'ler), Sık Kullanılanlar (yıldızlı). Arama, sık kullanılan işaretleme, satır menüsü, liste / kart görünümü.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly AppSettings _settings;
    private readonly AppDatabase _db;
    private List<LibraryItem> _all = new();
    private CancellationTokenSource? _scanCts;

    public LibraryViewModel(MainViewModel main, AppSettings settings, AppDatabase db)
    {
        _main = main; _settings = settings; _db = db;
    }

    public ObservableCollection<LibraryItem> Items { get; } = new();
    [ObservableProperty] private LibraryTab _tab = LibraryTab.Recent;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isCardView;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _emptyText = "";

    // Gösterge paneli (Ana Sayfa)
    [ObservableProperty] private int _recentCount;
    [ObservableProperty] private int _favoriteCount;
    [ObservableProperty] private int _monthOpened;
    [ObservableProperty] private int _monthConverted;
    [ObservableProperty] private int _monthOcr;
    public string DashboardText => $"Bu ay: {MonthOpened} belge açıldı\n{MonthConverted} dönüştürme, {MonthOcr} OCR işlemi";

    partial void OnTabChanged(LibraryTab value) => _ = RefreshAsync();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>Gösterge paneli sayılarını yeniler (veritabanından).</summary>
    public void RefreshStats()
    {
        try
        {
            var month = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
            var recent = _settings.KeepHistory ? _db.GetRecent(1000) : Array.Empty<RecentFile>();
            RecentCount = Math.Min(recent.Count, _settings.RecentLimit);
            FavoriteCount = recent.Count(r => r.Pinned);
            MonthOpened = _db.CountOpenedSince(month);
            MonthConverted = _db.CountTasksSince(month, "convert", "office-pdf", "compress", "export", "images-to-pdf", "batch");
            MonthOcr = _db.CountTasksSince(month, "ocr");
        }
        catch (Exception ex) { Log.Warn("Gösterge paneli okunamadı: " + ex.Message); }
        OnPropertyChanged(nameof(DashboardText));
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var items = new Dictionary<string, LibraryItem>(StringComparer.OrdinalIgnoreCase);
        var history = _settings.KeepHistory ? _db.GetRecent(Tab == LibraryTab.Recent ? _settings.RecentLimit : 5000) : Array.Empty<RecentFile>();
        foreach (var r in history)
        {
            if (Tab == LibraryTab.Favorites && !r.Pinned) continue;
            items[r.Path] = Make(r.Path, r.Pinned, r.PageCount, r.LastOpened, r.Size);
        }
        if (Tab == LibraryTab.Recent)
            _all = items.Values.OrderByDescending(i => i.Pinned).ThenByDescending(i => history.First(h => h.Path == i.Path).LastOpened).ToList();
        else _all = items.Values.OrderByDescending(i => i.Modified).ToList();
        ApplyFilter();
        RefreshStats();
        if (Tab != LibraryTab.All) return;

        // Tüm Belgeler: kullanıcı klasörlerindeki PDF'ler (arka planda, sınırlı derinlik).
        IsScanning = true;
        try
        {
            var pinned = history.Where(h => h.Pinned).Select(h => h.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = await Task.Run(() => ScanFolders(cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var f in found)
                if (!items.ContainsKey(f.FullName))
                    items[f.FullName] = new LibraryItem { Path = f.FullName, Modified = f.LastWriteTime, Size = f.Length, Exists = true, Pinned = pinned.Contains(f.FullName) };
            _all = items.Values.OrderByDescending(i => i.Modified).ToList();
            ApplyFilter();
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(_scanCts, cts)) IsScanning = false; }
    }

    private static LibraryItem Make(string path, bool pinned, int pages, DateTime opened, long size)
    {
        bool exists = File.Exists(path);
        DateTime modified = opened; long len = size;
        if (exists) try { var fi = new FileInfo(path); modified = fi.LastWriteTime; len = fi.Length; } catch { }
        return new LibraryItem { Path = path, Pinned = pinned, PageCount = pages, Modified = modified, Size = len, Exists = exists };
    }

    /// <summary>Belgeler, Masaüstü ve İndirilenler (2 düzey derinlik, en çok 3000 dosya).</summary>
    public static List<FileInfo> ScanFolders(CancellationToken ct)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase);
        var list = new List<FileInfo>();
        void Walk(string dir, int depth)
        {
            if (list.Count >= 3000 || ct.IsCancellationRequested) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.pdf"))
                {
                    try { list.Add(new FileInfo(f)); } catch { }
                    if (list.Count >= 3000) return;
                }
                if (depth >= 2) return;
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(d);
                    if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    try { if ((File.GetAttributes(d) & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue; } catch { continue; }
                    Walk(d, depth + 1);
                }
            }
            catch { }
        }
        foreach (var r in roots) Walk(r, 0);
        return list;
    }

    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        var view = q.Length == 0 ? _all : _all.Where(i => i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.Folder.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        Items.Clear();
        foreach (var i in view) Items.Add(i);
        EmptyText = Items.Count > 0 ? "" : q.Length > 0 ? $"“{q}” ile eşleşen belge yok."
            : Tab switch
            {
                LibraryTab.Favorites => "Sık kullanılan belge yok. Bir belgenin yanındaki yıldıza tıklayarak buraya ekleyin.",
                LibraryTab.All => IsScanning ? "Belgeler aranıyor…" : "Bilgisayarınızda PDF bulunamadı.",
                _ => _settings.KeepHistory ? "Henüz belge açılmadı. “PDF Aç” ile bir belge açın." : "Belge geçmişi Ayarlar'da kapatılmış.",
            };
    }

    [RelayCommand]
    private async Task Open(LibraryItem? item)
    {
        if (item is null) return;
        if (!File.Exists(item.Path))
        {
            if (MessageDialog.Confirm("Dosya bulunamadı", $"“{item.Name}” taşınmış veya silinmiş olabilir.\n{item.Path}\n\nListeden kaldırılsın mı?", "Kaldır", "Vazgeç"))
                await Remove(item);
            return;
        }
        await _main.OpenFilesAsync(new[] { item.Path });
    }

    [RelayCommand]
    private void OpenInNewWindow(LibraryItem? item)
    {
        if (item is null || !File.Exists(item.Path)) return;
        NewWindow.Start(item.Path);
    }

    [RelayCommand]
    private void TogglePin(LibraryItem? item)
    {
        if (item is null) return;
        bool pin = !item.Pinned;
        try
        {
            // Geçmişte yoksa önce ekle (Tüm Belgeler'den yıldızlanan dosya)
            if (!_db.GetRecent(5000).Any(r => string.Equals(r.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
                _db.AddRecent(item.Path, item.PageCount, item.Size);
            _db.SetPinned(item.Path, pin);
            item.Pinned = pin;
            if (Tab == LibraryTab.Favorites && !pin) { _all.Remove(item); Items.Remove(item); }
            RefreshStats();
            _main.LoadRecent();
        }
        catch (Exception ex) { MessageDialog.Show("Sık kullanılanlar", ex.Message, MessageKind.Error); }
    }

    [RelayCommand]
    private Task Remove(LibraryItem? item)
    {
        if (item is null) return Task.CompletedTask;
        try { _db.RemoveRecent(item.Path); } catch { }
        _all.Remove(item); Items.Remove(item);
        RefreshStats();
        _main.LoadRecent();
        ApplyFilter();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ShowInFolder(LibraryItem? item)
    {
        if (item is null || !File.Exists(item.Path)) return;
        try { Process.Start("explorer.exe", $"/select,\"{item.Path}\""); } catch { }
    }

    [RelayCommand]
    private void ConvertItem(LibraryItem? item)
    {
        if (item is null || !File.Exists(item.Path)) return;
        _main.Converter.SetSource(item.Path);
        _main.Navigate(AppScreen.Convert);
    }

    [RelayCommand]
    private void OcrItem(LibraryItem? item)
    {
        if (item is null || !File.Exists(item.Path)) return;
        _main.OcrScreen.SetSource(item.Path);
        _main.Navigate(AppScreen.Ocr);
    }

    [RelayCommand] private void ToggleView() => IsCardView = !IsCardView;

    [RelayCommand] private void OpenNewWindow() => NewWindow.Start(null);
}

/// <summary>Bağımsız ikinci uygulama penceresi (tek örnek denetimi atlanır).</summary>
public static class NewWindow
{
    public static void Start(string? file)
    {
        try
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is null) return;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--new-window");
            if (file is not null) psi.ArgumentList.Add(file);
            Process.Start(psi);
        }
        catch (Exception ex) { MessageDialog.Show("Yeni pencere açılamadı", ex.Message, MessageKind.Error); }
    }
}
