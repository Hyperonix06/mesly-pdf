using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Core;
using MeslyPdfReader.Data;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.ViewModels;

/// <summary>Sayfa küçük resmi. Görüntü, öğe ekranda ilk kez gösterildiğinde (sanallaştırma sayesinde) yüklenir.</summary>
public sealed partial class ThumbnailItem : ObservableObject
{
    public const double ThumbWidth = 132;
    private readonly PdfDocument _doc;
    private readonly int _revision;
    private bool _loading;
    private ImageSource? _image;

    public ThumbnailItem(PdfDocument doc, int index)
    {
        _doc = doc; Index = index; _revision = doc.Revision;
        var s = doc.GetPageSize(index);
        DisplayHeight = Math.Clamp(ThumbWidth * s.Height / Math.Max(1, s.Width), 40, 260);
    }

    public int Index { get; }
    public string Label => (Index + 1).ToString();
    public double DisplayWidth => ThumbWidth;
    public double DisplayHeight { get; }
    [ObservableProperty] private bool _isCurrent;

    public ImageSource? Image
    {
        get
        {
            if (_image is null && !_loading) _ = LoadAsync();
            return _image;
        }
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var dpi = 1.5;
            int w = (int)(ThumbWidth * dpi), h = (int)(DisplayHeight * dpi);
            var img = await _doc.RenderAsync(Index, new RenderRequest(w, h), lowPriority: true);
            if (_doc.Revision != _revision) return;
            _image = await Task.Run(() => ImageCodec.ToBitmapSource(img, 96 * dpi));
            OnPropertyChanged(nameof(Image));
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log.Warn($"Küçük resim {Index + 1}: {ex.Message}"); }
        finally { _loading = false; }
    }
}

public sealed partial class BookmarkNode : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private int _pageIndex = -1;
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isSelected;
    public string? Uri { get; set; }
    public BookmarkNode? Parent { get; set; }
    public ObservableCollection<BookmarkNode> Children { get; } = new();
    public string PageLabel => Uri is not null ? "bağlantı" : PageIndex >= 0 ? $"s. {PageIndex + 1}" : "";

    partial void OnPageIndexChanged(int value) => OnPropertyChanged(nameof(PageLabel));

    public static BookmarkNode From(Bookmark b, BookmarkNode? parent)
    {
        var n = new BookmarkNode { Title = b.Title, PageIndex = b.PageIndex, Uri = b.Uri, Parent = parent, IsExpanded = false };
        foreach (var c in b.Children) n.Children.Add(From(c, n));
        return n;
    }

    public Bookmark ToModel()
    {
        var b = new Bookmark { Title = Title, PageIndex = PageIndex, Uri = Uri };
        foreach (var c in Children) b.Children.Add(c.ToModel());
        return b;
    }
}

public sealed class SearchResultItem
{
    public required SearchHit Hit { get; init; }
    public string PageLabel => $"Sayfa {Hit.PageIndex + 1}";
    public string Context => Hit.Context;
}

public sealed class RecentItem
{
    public required RecentFile File { get; init; }
    public string Name => File.Name;
    public string Folder => File.Folder;
    public string Info
    {
        get
        {
            var parts = new List<string>();
            if (File.PageCount > 0) parts.Add($"{File.PageCount} sayfa");
            if (File.Size > 0) parts.Add(FormatSize(File.Size));
            parts.Add(FormatWhen(File.LastOpened));
            return string.Join(" · ", parts);
        }
    }
    public bool Exists => File.Exists;
    public bool Pinned => File.Pinned;

    public static string FormatSize(long b) => b < 1024 ? $"{b} B" : b < 1048576 ? $"{b / 1024.0:0.#} KB" : b < 1073741824 ? $"{b / 1048576.0:0.#} MB" : $"{b / 1073741824.0:0.##} GB";

    public static string FormatWhen(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return "az önce";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} dk önce";
        if (t.Date == DateTime.Today) return $"bugün {t:HH:mm}";
        if (t.Date == DateTime.Today.AddDays(-1)) return $"dün {t:HH:mm}";
        return t.ToString("d MMMM yyyy", TextSearch.Turkish);
    }
}
