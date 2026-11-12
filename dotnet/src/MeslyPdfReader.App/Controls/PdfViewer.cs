using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.Controls;

public enum ZoomMode { Custom, FitWidth, FitPage, ActualSize }
public enum PageLayoutMode { Continuous, SinglePage, TwoPageContinuous, TwoPage }
public enum ViewerTool { Select, Hand, Edit, Highlight, Underline, StrikeOut, Note }

/// <summary>Metin işaretleme açıklaması isteği: seçilen metnin satır dikdörtgenleri (PDF koordinatı).</summary>
public sealed record MarkupRequest(ViewerTool Kind, int Page, IReadOnlyList<RectF> Rects, string Text);

public readonly record struct TextPosition(int Page, int Char) : IComparable<TextPosition>
{
    public int CompareTo(TextPosition o) => Page != o.Page ? Page.CompareTo(o.Page) : Char.CompareTo(o.Char);
}

/// <summary>
/// Sanal kaydırmalı PDF görüntüleyici. Yalnızca görünen sayfalar çizilir; çizim istekleri PDF motoruna
/// öncelikli ve iptal edilebilir olarak gönderilir. Arayüz iş parçacığı PDF motorunu hiç beklemez.
/// </summary>
public sealed partial class PdfViewer : FrameworkElement, IScrollInfo
{
    private const double PageMargin = 18, Gap = 14;
    private const long FullRenderPixelLimit = 14_000_000;
    public static readonly double[] ZoomSteps = { 0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6.4, 8 };
    public const double MinZoom = 0.1, MaxZoom = 8;

    // ─────────────── Bağımlılık özellikleri ───────────────

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(nameof(Document), typeof(PdfDocument), typeof(PdfViewer),
        new PropertyMetadata(null, (d, e) => ((PdfViewer)d).OnDocumentSet((PdfDocument?)e.OldValue, (PdfDocument?)e.NewValue)));
    public PdfDocument? Document { get => (PdfDocument?)GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(PdfViewer),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((PdfViewer)d).OnZoomChanged(), (d, v) => Math.Clamp((double)v, MinZoom, MaxZoom)));
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    public static readonly DependencyProperty ZoomModeProperty = DependencyProperty.Register(nameof(ZoomMode), typeof(ZoomMode), typeof(PdfViewer),
        new FrameworkPropertyMetadata(ZoomMode.FitWidth, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((PdfViewer)d).ApplyZoomMode(keepPage: true)));
    public ZoomMode ZoomMode { get => (ZoomMode)GetValue(ZoomModeProperty); set => SetValue(ZoomModeProperty, value); }

    public static readonly DependencyProperty LayoutModeProperty = DependencyProperty.Register(nameof(LayoutMode), typeof(PageLayoutMode), typeof(PdfViewer),
        new FrameworkPropertyMetadata(PageLayoutMode.Continuous, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((PdfViewer)d).OnLayoutModeChanged()));
    public PageLayoutMode LayoutMode { get => (PageLayoutMode)GetValue(LayoutModeProperty); set => SetValue(LayoutModeProperty, value); }

    public static readonly DependencyProperty ViewRotationProperty = DependencyProperty.Register(nameof(ViewRotation), typeof(int), typeof(PdfViewer),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((PdfViewer)d).Relayout(keepPage: true, clearCache: true)));
    /// <summary>Görünüm döndürmesi (yalnızca ekranda), 90° adımları.</summary>
    public int ViewRotation { get => (int)GetValue(ViewRotationProperty); set => SetValue(ViewRotationProperty, value); }

    public static readonly DependencyProperty DarkModeProperty = DependencyProperty.Register(nameof(DarkMode), typeof(bool), typeof(PdfViewer),
        new PropertyMetadata(false, (d, e) => ((PdfViewer)d).ClearAndRender()));
    public bool DarkMode { get => (bool)GetValue(DarkModeProperty); set => SetValue(DarkModeProperty, value); }

    public static readonly DependencyProperty ToolProperty = DependencyProperty.Register(nameof(Tool), typeof(ViewerTool), typeof(PdfViewer),
        new PropertyMetadata(ViewerTool.Select, (d, e) => ((PdfViewer)d).OnToolChanged()));
    public ViewerTool Tool { get => (ViewerTool)GetValue(ToolProperty); set => SetValue(ToolProperty, value); }

    public static readonly DependencyProperty CurrentPageProperty = DependencyProperty.Register(nameof(CurrentPage), typeof(int), typeof(PdfViewer),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((PdfViewer)d).OnCurrentPageSet((int)e.NewValue)));
    /// <summary>Geçerli sayfa (0 tabanlı). Kaydırmayla güncellenir; atanınca o sayfaya gider.</summary>
    public int CurrentPage { get => (int)GetValue(CurrentPageProperty); set => SetValue(CurrentPageProperty, value); }

    public static readonly DependencyProperty SearchHitsProperty = DependencyProperty.Register(nameof(SearchHits), typeof(IReadOnlyList<SearchHit>), typeof(PdfViewer),
        new PropertyMetadata(null, (d, e) => ((PdfViewer)d).OnSearchHitsChanged()));
    public IReadOnlyList<SearchHit>? SearchHits { get => (IReadOnlyList<SearchHit>?)GetValue(SearchHitsProperty); set => SetValue(SearchHitsProperty, value); }

    public static readonly DependencyProperty ActiveHitProperty = DependencyProperty.Register(nameof(ActiveHit), typeof(SearchHit), typeof(PdfViewer),
        new PropertyMetadata(null, (d, e) => ((PdfViewer)d).OnActiveHitChanged()));
    public SearchHit? ActiveHit { get => (SearchHit?)GetValue(ActiveHitProperty); set => SetValue(ActiveHitProperty, value); }

    public event EventHandler<LinkInfo>? LinkActivated;
    public event EventHandler? SelectionChanged;
    public event EventHandler<string>? StatusMessage;
    /// <summary>Vurgula / altını çiz / üstünü çiz aracıyla metin seçildi (sayfa başına bir istek).</summary>
    public event EventHandler<MarkupRequest>? MarkupRequested;
    /// <summary>Not aracıyla sayfaya tıklandı: (sayfa, x, y) PDF koordinatı.</summary>
    public event EventHandler<(int Page, double X, double Y)>? NoteRequested;

    private bool MarkupTool => Tool is ViewerTool.Highlight or ViewerTool.Underline or ViewerTool.StrikeOut;

    // ─────────────── Durum ───────────────

    private sealed class Row { public int First; public int Count; public double Top; public double Height; }
    private readonly List<Row> _rows = new();
    private Rect[] _pageRects = Array.Empty<Rect>();   // düzen alanında; yalnız düzene dahil sayfalar dolu
    private bool[] _inLayout = Array.Empty<bool>();
    private double _extentW, _extentH;
    private Size _viewport;
    private Vector _offset;
    private int _singleRowPage;                        // tek sayfa kipinde gösterilen sayfa
    private bool _suppressPageSync;
    private readonly RenderCache _cache = new(384L * 1024 * 1024);
    private readonly Dictionary<int, (RenderKey Key, CancellationTokenSource Cts)> _inflight = new();
    private readonly Dictionary<int, PageAux> _aux = new();
    private readonly Dictionary<int, string> _errors = new();
    private bool _renderQueued;
    private int _revision = -1;
    private DpiScale _dpi = new(1, 1);

    private sealed class PageAux
    {
        public int Revision;
        public PageGeometry? Geometry;
        public IReadOnlyList<LinkInfo>? Links;
        public IReadOnlyList<AnnotationInfo>? Notes;
        public PageText? Text;
        public List<EditItem>? EditItems;
        public bool LoadingBasic, LoadingText, LoadingEdit;
    }

    // seçim
    private TextPosition? _selAnchor, _selFocus;
    private bool _selecting, _panning, _linkCandidate;
    private Point _mouseDown, _panStartOffset;
    private LinkInfo? _hoverLink;
    private readonly DispatcherTimer _autoScroll;

    private static readonly Brush ShadowBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)));
    private static readonly Brush ShadowBrush2 = Freeze(new SolidColorBrush(Color.FromArgb(0x10, 0, 0, 0)));
    private static readonly Brush PageLight = Brushes.White;
    private static readonly Brush PageDark = Freeze(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)));
    private static readonly Brush HitBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xD5, 0x00)));
    private static readonly Brush ActiveHitBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0x8A, 0x00)));
    private static readonly Pen ActiveHitPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x00)), 1.5));
    private static readonly Brush SelBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0x3D, 0x7B, 0xFD)));
    private static readonly Brush LinkHoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x3D, 0x7B, 0xFD)));
    private static readonly Typeface UiFace = new("Segoe UI");


    public PdfViewer()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        _autoScroll = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) => AutoScrollTick(), Dispatcher) { IsEnabled = false };
        Loaded += (_, _) => { _dpi = VisualTreeHelper.GetDpi(this); QueueRender(); };
        Unloaded += (_, _) => CancelAll();
    }

    public long CacheBudgetBytes { get => _cache.Budget; set { _cache.Budget = Math.Max(64L << 20, value); _cache.Trim(); } }
    public long CacheBytes => _cache.Bytes;
    public int PageCount => Document?.PageCount ?? 0;

    /// <summary>Test/öz test için: bekleyen çizim isteği sayısı.</summary>
    public int PendingRenders => _inflight.Count;

    // ─────────────── Belge ───────────────

    private void OnDocumentSet(PdfDocument? oldDoc, PdfDocument? newDoc)
    {
        if (oldDoc is not null) oldDoc.Changed -= OnDocChanged;
        CancelAll();
        _cache.Clear(); _aux.Clear(); _errors.Clear();
        ClearSelection();
        CommitEditor(cancel: true);
        SetSelection(null);
        _offset = default;
        _singleRowPage = 0;
        if (newDoc is not null)
        {
            newDoc.Changed += OnDocChanged;
            _revision = newDoc.Revision;
        }
        ApplyZoomMode(keepPage: false);
    }

    private void OnDocChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (!ReferenceEquals(sender, Document)) return;
        var doc = Document!;
        if (doc.Revision == _revision) return;
        _revision = doc.Revision;
        CancelAll();
        _cache.Clear(); _aux.Clear(); _errors.Clear();
        ClearSelection();
        _hoverItem = null;
        if (_sel is not null) SetSelection(null);
        int page = Math.Clamp(CurrentPage, 0, Math.Max(0, doc.PageCount - 1));
        _singleRowPage = page;
        if (ZoomMode != ZoomMode.Custom) ApplyZoomMode(keepPage: true);
        else Relayout(keepPage: true);
        if (CurrentPage != page) SetCurrentPageSilently(page);
    }, DispatcherPriority.Normal);

    private void OnLayoutModeChanged()
    {
        if (ZoomMode != ZoomMode.Custom) ApplyZoomMode(keepPage: true);
        else Relayout(keepPage: true);
    }

    private void ClearAndRender()
    {
        CancelAll();
        _cache.Clear();
        InvalidateVisual();
        QueueRender();
    }

    // ─────────────── Yakınlaştırma ───────────────

    private bool _applyingZoomMode;

    private void OnZoomChanged()
    {
        if (!_applyingZoomMode && ZoomMode != ZoomMode.Custom)
            SetCurrentValue(ZoomModeProperty, ZoomMode.Custom);
        Relayout(keepPage: true);
    }

    private void ApplyZoomMode(bool keepPage)
    {
        var doc = Document;
        if (doc is null || _viewport.Width <= 0) { Relayout(keepPage); return; }
        double z = Zoom;
        int cols = LayoutMode is PageLayoutMode.TwoPage or PageLayoutMode.TwoPageContinuous ? 2 : 1;
        switch (ZoomMode)
        {
            case ZoomMode.FitWidth:
                {
                    double maxW = 0;
                    var sizes = doc.PageSizes;
                    int step = Math.Max(1, sizes.Count / 400); // büyük belgelerde örnekle
                    for (int i = 0; i < sizes.Count; i += step) maxW = Math.Max(maxW, DisplaySize(i, 1).Width);
                    if (maxW <= 0) break;
                    double avail = _viewport.Width - 2 * PageMargin - (cols - 1) * Gap - 2;
                    z = avail / (maxW * cols);
                    break;
                }
            case ZoomMode.FitPage:
                {
                    var s = DisplaySize(Math.Clamp(CurrentPage, 0, doc.PageCount - 1), 1);
                    double zw = (_viewport.Width - 2 * PageMargin - (cols - 1) * Gap) / (s.Width * cols);
                    double zh = (_viewport.Height - 2 * PageMargin) / s.Height;
                    z = Math.Min(zw, zh);
                    break;
                }
            case ZoomMode.ActualSize: z = 1; break;
        }
        _applyingZoomMode = true;
        try
        {
            z = Math.Clamp(z, MinZoom, MaxZoom);
            if (Math.Abs(z - Zoom) > 1e-6) SetCurrentValue(ZoomProperty, z); // OnZoomChanged → Relayout
            else Relayout(keepPage);
        }
        finally { _applyingZoomMode = false; }
    }

    public void ZoomIn() => ZoomTo(ZoomSteps.FirstOrDefault(s => s >= Zoom * 1.05, MaxZoom), null);
    public void ZoomOut() => ZoomTo(ZoomSteps.LastOrDefault(s => s <= Zoom / 1.05, MinZoom), null);

    /// <summary>Verilen noktayı (görünüm koordinatı) sabit tutarak yakınlaştırır.</summary>
    public void ZoomTo(double zoom, Point? anchor)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        var a = anchor ?? new Point(_viewport.Width / 2, _viewport.Height / 2);
        var docPt = ViewToPageUnit(a);
        SetCurrentValue(ZoomModeProperty, ZoomMode.Custom);
        _suppressPageSync = true;
        try { SetCurrentValue(ZoomProperty, zoom); }
        finally { _suppressPageSync = false; }
        if (docPt is { } p && p.Page < _pageRects.Length && _inLayout[p.Page])
        {
            var r = _pageRects[p.Page];
            double nx = r.X + p.U * r.Width - a.X, ny = r.Y + p.V * r.Height - a.Y;
            SetOffsets(nx, ny);
        }
    }

    private Size DisplaySize(int page, double zoom)
    {
        var s = Document!.GetPageSize(page);
        double w = s.Width / 72.0 * 96.0 * zoom, h = s.Height / 72.0 * 96.0 * zoom;
        return (ViewRotation % 2 == 1) ? new Size(h, w) : new Size(w, h);
    }

    // ─────────────── Düzen ───────────────

    private void Relayout(bool keepPage, bool clearCache = false)
    {
        int keep = CurrentPage;
        double keepFrac = 0;
        if (keepPage && keep < _pageRects.Length && _inLayout.Length > keep && _inLayout[keep])
        {
            var r = _pageRects[keep];
            keepFrac = r.Height > 0 ? (_offset.Y - r.Y) / r.Height : 0;
        }
        if (clearCache) { CancelAll(); _cache.Clear(); }
        if (LayoutMode is PageLayoutMode.SinglePage or PageLayoutMode.TwoPage) _singleRowPage = CurrentPage;
        BuildLayout();
        if (_editor is not null) { InvalidateMeasure(); InvalidateArrange(); }
        if (keepPage && Document is not null && keep < _pageRects.Length && _inLayout[keep])
        {
            var r = _pageRects[keep];
            SetOffsets(_offset.X, r.Y + keepFrac * r.Height, syncPage: false);
        }
        else SetOffsets(_offset.X, _offset.Y, syncPage: false);
        InvalidateVisual();
        QueueRender();
    }

    private void BuildLayout()
    {
        _rows.Clear();
        var doc = Document;
        int n = doc?.PageCount ?? 0;
        _pageRects = new Rect[n];
        _inLayout = new bool[n];
        _extentW = _extentH = 0;
        if (doc is null || n == 0) { ScrollOwner?.InvalidateScrollInfo(); return; }

        bool two = LayoutMode is PageLayoutMode.TwoPage or PageLayoutMode.TwoPageContinuous;
        bool continuous = LayoutMode is PageLayoutMode.Continuous or PageLayoutMode.TwoPageContinuous;
        int first = 0, last = n - 1;
        if (!continuous)
        {
            int p = Math.Clamp(_singleRowPage, 0, n - 1);
            if (two) p -= p % 2;
            first = p; last = Math.Min(n - 1, p + (two ? 1 : 0));
        }
        double z = Zoom, y = PageMargin, maxRowW = 0;
        var rowWidths = new List<double>();
        for (int i = first; i <= last;)
        {
            int count = two ? Math.Min(2, last - i + 1) : 1;
            double h = 0, w = 0;
            for (int k = 0; k < count; k++) { var s = DisplaySize(i + k, z); h = Math.Max(h, s.Height); w += s.Width; }
            w += (count - 1) * Gap;
            _rows.Add(new Row { First = i, Count = count, Top = y, Height = h });
            rowWidths.Add(w);
            maxRowW = Math.Max(maxRowW, w);
            y += h + Gap;
            i += count;
        }
        _extentH = y - Gap + PageMargin;
        _extentW = Math.Max(maxRowW + 2 * PageMargin, _viewport.Width);
        for (int r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            double x = (_extentW - rowWidths[r]) / 2;
            for (int k = 0; k < row.Count; k++)
            {
                int i = row.First + k;
                var s = DisplaySize(i, z);
                _pageRects[i] = new Rect(x, row.Top + (row.Height - s.Height) / 2, s.Width, s.Height);
                _inLayout[i] = true;
                x += s.Width + Gap;
            }
        }
        ScrollOwner?.InvalidateScrollInfo();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var w = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height;
        MeasureEditor();
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize != _viewport)
        {
            _viewport = finalSize;
            if (ZoomMode != ZoomMode.Custom) ApplyZoomMode(keepPage: true);
            else Relayout(keepPage: true);
        }
        ArrangeEditor();
        return finalSize;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _dpi = newDpi;
        ClearAndRender();
        base.OnDpiChanged(oldDpi, newDpi);
    }

    // ─────────────── Gezinme ───────────────

    private void OnCurrentPageSet(int page)
    {
        if (_suppressPageSync) return;
        GoToPage(page);
    }

    private void SetCurrentPageSilently(int page)
    {
        if (CurrentPage == page) return;
        _suppressPageSync = true;
        try { SetCurrentValue(CurrentPageProperty, page); }
        finally { _suppressPageSync = false; }
    }

    public void GoToPage(int page, double unitY = 0)
    {
        var doc = Document;
        if (doc is null || doc.PageCount == 0) return;
        page = Math.Clamp(page, 0, doc.PageCount - 1);
        bool continuous = LayoutMode is PageLayoutMode.Continuous or PageLayoutMode.TwoPageContinuous;
        if (!continuous && !_inLayout[page])
        {
            _singleRowPage = page;
            BuildLayout();
            _offset = default;
            InvalidateVisual();
            QueueRender();
        }
        var r = _pageRects[page];
        SetOffsets(_offset.X, r.Y - PageMargin / 2 + unitY * r.Height, syncPage: false);
        SetCurrentPageSilently(page);
    }

    public void NextPage() => StepPage(+1);
    public void PreviousPage() => StepPage(-1);

    private void StepPage(int dir)
    {
        int step = LayoutMode is PageLayoutMode.TwoPage or PageLayoutMode.TwoPageContinuous ? 2 : 1;
        int target = CurrentPage + dir * step;
        if (step == 2) target -= target % 2;
        GoToPage(target);
    }

    /// <summary>Bir arama sonucunu görünür alana getirir.</summary>
    public void RevealHit(SearchHit hit)
    {
        GoToPage(hit.PageIndex);
        EnsureBasic(hit.PageIndex, then: () =>
        {
            if (!TryGetViewRect(hit.PageIndex, hit.Rects.FirstOrDefault(), out var vr)) return;
            double targetY = _offset.Y + vr.Y - _viewport.Height / 3;
            double targetX = _offset.X;
            if (vr.X < 0 || vr.Right > _viewport.Width) targetX = _offset.X + vr.X - _viewport.Width / 3;
            SetOffsets(targetX, targetY);
        });
    }

    private void UpdateCurrentPageFromScroll()
    {
        if (_rows.Count == 0 || Document is null) return;
        bool continuous = LayoutMode is PageLayoutMode.Continuous or PageLayoutMode.TwoPageContinuous;
        if (!continuous) { SetCurrentPageSilently(_rows[0].First); return; }
        double probe = _offset.Y + Math.Min(_viewport.Height * 0.35, 260);
        int idx = FindRow(probe);
        if (idx >= 0) SetCurrentPageSilently(_rows[idx].First);
    }

    private int FindRow(double y)
    {
        int lo = 0, hi = _rows.Count - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_rows[mid].Top <= y) { ans = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return ans;
    }

    /// <summary>Görünüm alanında (kısmen) görünen sayfalar.</summary>
    public IReadOnlyList<int> VisiblePages(double extra = 0)
    {
        var list = new List<int>();
        if (_rows.Count == 0) return list;
        double top = _offset.Y - extra, bottom = _offset.Y + _viewport.Height + extra;
        for (int r = Math.Max(0, FindRow(top) - 1); r < _rows.Count; r++)
        {
            var row = _rows[r];
            if (row.Top > bottom) break;
            if (row.Top + row.Height < top) continue;
            for (int k = 0; k < row.Count; k++) list.Add(row.First + k);
        }
        return list;
    }

    private Rect ViewRect(int page)
    {
        var r = _pageRects[page];
        // Aygıt piksellerine hizala: bit eşlemler bulanıklaşmasın.
        double sx = _dpi.DpiScaleX, sy = _dpi.DpiScaleY;
        double x = Math.Round((r.X - _offset.X) * sx) / sx, y = Math.Round((r.Y - _offset.Y) * sy) / sy;
        double w = Math.Round(r.Width * sx) / sx, h = Math.Round(r.Height * sy) / sy;
        return new Rect(x, y, w, h);
    }

    private bool TryGetViewRect(int page, RectF pdfRect, out Rect rect)
    {
        rect = default;
        if (!_inLayout[page] || !_aux.TryGetValue(page, out var aux) || aux.Geometry is not { } g || pdfRect.IsEmpty) return false;
        var vr = ViewRect(page);
        var (x, y, w, h) = g.ToDevice(pdfRect, vr.X, vr.Y, vr.Width, vr.Height, ViewRotation);
        rect = new Rect(x, y, w, h);
        return true;
    }

    private (int Page, double U, double V)? ViewToPageUnit(Point p)
    {
        foreach (var i in VisiblePages())
        {
            var r = ViewRect(i);
            if (r.Contains(p)) return (i, (p.X - r.X) / r.Width, (p.Y - r.Y) / r.Height);
        }
        // sayfalar arası boşluk: en yakın sayfa
        var vis = VisiblePages();
        if (vis.Count == 0) return null;
        int best = vis.OrderBy(i => DistanceTo(ViewRect(i), p)).First();
        var br = ViewRect(best);
        return (best, Math.Clamp((p.X - br.X) / br.Width, 0, 1), Math.Clamp((p.Y - br.Y) / br.Height, 0, 1));
    }

    private static double DistanceTo(Rect r, Point p)
    {
        double dx = p.X < r.Left ? r.Left - p.X : p.X > r.Right ? p.X - r.Right : 0;
        double dy = p.Y < r.Top ? r.Top - p.Y : p.Y > r.Bottom ? p.Y - r.Bottom : 0;
        return dx * dx + dy * dy;
    }

    // ─────────────── Çizim ───────────────

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var doc = Document;
        if (doc is null) return;
        var visible = VisiblePages();
        foreach (var i in visible)
        {
            var r = ViewRect(i);
            dc.DrawRectangle(ShadowBrush2, null, new Rect(r.X - 1, r.Y, r.Width + 2, r.Height + 3));
            dc.DrawRectangle(ShadowBrush, null, new Rect(r.X, r.Y + 1, r.Width, r.Height + 1));
            dc.DrawRectangle(DarkMode ? PageDark : PageLight, null, r);

            var full = _cache.GetFull(i);
            if (full is not null) dc.DrawImage(full.Bitmap, r);
            var region = _cache.GetRegion(i);
            if (region is not null && region.Key.Width == (int)Math.Round(r.Width * _dpi.DpiScaleX))
            {
                var k = region.Key;
                dc.DrawImage(region.Bitmap, new Rect(r.X + k.RX / _dpi.DpiScaleX, r.Y + k.RY / _dpi.DpiScaleY, k.RW / _dpi.DpiScaleX, k.RH / _dpi.DpiScaleY));
            }
            if (full is null && region is null)
            {
                var msg = _errors.TryGetValue(i, out var err) ? err : $"Sayfa {i + 1} yükleniyor…";
                var ft = new FormattedText(msg, Core.TextSearch.Turkish, FlowDirection.LeftToRight, UiFace, 12,
                    _errors.ContainsKey(i) ? Brushes.IndianRed : Brushes.Gray, _dpi.PixelsPerDip);
                ft.MaxTextWidth = Math.Max(40, r.Width - 20);
                dc.DrawText(ft, new Point(r.X + 10, r.Y + 10));
            }
            DrawOverlays(dc, i, r);
        }
    }

    private void DrawOverlays(DrawingContext dc, int page, Rect r)
    {
        if (!_aux.TryGetValue(page, out var aux) || aux.Geometry is not { } g) return;
        dc.PushClip(new RectangleGeometry(r));
        var hits = SearchHits;
        if (hits is not null)
            foreach (var h in HitsForPage(hits, page))
            {
                bool active = ReferenceEquals(h, ActiveHit);
                foreach (var rc in h.Rects)
                {
                    var (x, y, w, hh) = g.ToDevice(rc, r.X, r.Y, r.Width, r.Height, ViewRotation);
                    dc.DrawRoundedRectangle(active ? ActiveHitBrush : HitBrush, active ? ActiveHitPen : null, new Rect(x - 1, y - 1, w + 2, hh + 2), 2, 2);
                }
            }
        if (_selAnchor is { } a && _selFocus is { } f && aux.Text is { } text && text.Length > 0)
        {
            var (s, e) = a.CompareTo(f) <= 0 ? (a, f) : (f, a);
            if (page >= s.Page && page <= e.Page)
            {
                int start = page == s.Page ? s.Char : 0, end = page == e.Page ? e.Char : text.Length - 1;
                foreach (var rc in text.GetLineRects(start, end))
                {
                    var (x, y, w, hh) = g.ToDevice(rc, r.X, r.Y, r.Width, r.Height, ViewRotation);
                    dc.DrawRectangle(SelBrush, null, new Rect(x, y, w, hh));
                }
            }
        }
        if (_hoverLink is { } link && aux.Links?.Contains(link) == true)
        {
            var (x, y, w, hh) = g.ToDevice(link.Rect, r.X, r.Y, r.Width, r.Height, ViewRotation);
            dc.DrawRoundedRectangle(LinkHoverBrush, null, new Rect(x, y, w, hh), 2, 2);
        }
        if (ShowNoteCallouts && aux.Notes is { Count: > 0 } notes)
            foreach (var n in notes) DrawNoteCallout(dc, g, r, n);
        DrawEditOverlay(dc, page, r);
        dc.Pop();
    }

    /// <summary>Not açıklamalarının içeriğini simgenin yanında balonla gösterir (yalnız ekranda; belgeye yazılmaz).</summary>
    public bool ShowNoteCallouts { get; set; } = true;

    private static readonly Brush NoteBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFD, 0xE7, 0xE6)));
    private static readonly Pen NotePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xD9, 0x13, 0x15)), 1));
    private static readonly Typeface NoteFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface NoteBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private void DrawNoteCallout(DrawingContext dc, PageGeometry g, Rect r, AnnotationInfo n)
    {
        var (x, y, w, h) = g.ToDevice(n.Rect, r.X, r.Y, r.Width, r.Height, ViewRotation);
        double pxPerDip = _dpi.PixelsPerDip;
        var text = n.Contents.Length > 160 ? n.Contents[..157] + "…" : n.Contents;
        var head = new FormattedText("Açıklama:", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, NoteBold, 11, Brushes.Black, pxPerDip);
        var body = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, NoteFace, 11, Brushes.Black, pxPerDip) { MaxTextWidth = 180 };
        double bw = Math.Max(head.Width, body.Width) + 14, bh = head.Height + body.Height + 10;
        double bx = x + w + 6, by = y;
        if (bx + bw > r.Right) bx = Math.Max(r.Left, x - bw - 6);
        var box = new Rect(bx, by, bw, bh);
        dc.DrawRoundedRectangle(NoteBrush, NotePen, box, 4, 4);
        dc.DrawText(head, new Point(bx + 7, by + 4));
        dc.DrawText(body, new Point(bx + 7, by + 4 + head.Height));
    }

    /// <summary>Fare altındaki notun metni (ipucu için).</summary>
    private string? HitNote(Point p)
    {
        var u = ViewToPageUnit(p);
        if (u is not { } hit || !_aux.TryGetValue(hit.Page, out var aux) || aux.Notes is not { Count: > 0 } notes || aux.Geometry is not { } g) return null;
        var (x, y) = g.FromUnit(hit.U, hit.V, ViewRotation);
        return notes.FirstOrDefault(n => n.Rect.Contains(x, y))?.Contents;
    }

    private Dictionary<int, List<SearchHit>>? _hitsByPage;

    private IEnumerable<SearchHit> HitsForPage(IReadOnlyList<SearchHit> hits, int page)
    {
        if (_hitsByPage is null)
        {
            _hitsByPage = new();
            foreach (var h in hits)
            {
                if (!_hitsByPage.TryGetValue(h.PageIndex, out var l)) _hitsByPage[h.PageIndex] = l = new();
                l.Add(h);
            }
        }
        return _hitsByPage.TryGetValue(page, out var list) ? list : Enumerable.Empty<SearchHit>();
    }

    private void OnSearchHitsChanged() { _hitsByPage = null; InvalidateVisual(); }

    private void OnActiveHitChanged()
    {
        if (ActiveHit is { } h) RevealHit(h);
        InvalidateVisual();
    }

    // ─────────────── Çizim istekleri ───────────────

    private void QueueRender()
    {
        if (_renderQueued) return;
        _renderQueued = true;
        Dispatcher.BeginInvoke(() => { _renderQueued = false; ScheduleRenders(); }, DispatcherPriority.Background);
    }

    private void ScheduleRenders()
    {
        var doc = Document;
        if (doc is null || _viewport.Width <= 0 || !IsVisible) return;
        var visible = VisiblePages();
        var prefetch = VisiblePages(_viewport.Height * 0.8).Except(visible).ToList();
        var wanted = new HashSet<int>(visible.Concat(prefetch));

        // Görünmez olan sayfaların bekleyen isteklerini iptal et.
        foreach (var p in _inflight.Keys.Where(p => !wanted.Contains(p)).ToList())
        {
            _inflight[p].Cts.Cancel();
            _inflight.Remove(p);
        }
        _cache.DropRegionsExcept(visible.ToHashSet());

        double centerY = _offset.Y + _viewport.Height / 2;
        foreach (var i in visible.OrderBy(p => Math.Abs(_pageRects[p].Y + _pageRects[p].Height / 2 - centerY)))
        {
            EnsureBasic(i);
            RequestPage(i, allowRegion: true, lowPriority: false);
        }
        foreach (var i in prefetch) RequestPage(i, allowRegion: false, lowPriority: true);
        if (Tool == ViewerTool.Select || MarkupTool) foreach (var i in visible) EnsureText(i);
        if (Tool == ViewerTool.Edit) foreach (var i in visible) EnsureEditItems(i);
        _cache.Trim(wanted);
    }

    private void RequestPage(int i, bool allowRegion, bool lowPriority)
    {
        var doc = Document!;
        var r = _pageRects[i];
        int pw = Math.Max(1, (int)Math.Round(r.Width * _dpi.DpiScaleX));
        int ph = Math.Max(1, (int)Math.Round(r.Height * _dpi.DpiScaleY));
        int turns = ((ViewRotation % 4) + 4) % 4;
        bool dark = DarkMode;

        if ((long)pw * ph <= FullRenderPixelLimit)
        {
            var key = new RenderKey(i, doc.Revision, pw, ph, turns, dark, 0, 0, 0, 0);
            if (_cache.GetFull(i)?.Key == key) return;
            Start(i, key, new RenderRequest(pw, ph, QuarterTurns: turns), lowPriority);
            return;
        }
        // Çok yüksek yakınlaştırma: önce düşük çözünürlüklü tam sayfa, sonra yalnızca görünen bölge.
        double s = Math.Sqrt(2_500_000.0 / ((double)pw * ph));
        int lw = Math.Max(1, (int)(pw * s)), lh = Math.Max(1, (int)(ph * s));
        var lowKey = new RenderKey(i, doc.Revision, lw, lh, turns, dark, 0, 0, 0, 0);
        var existing = _cache.GetFull(i);
        bool needLow = existing is null || existing.Key.Revision != doc.Revision || existing.Key.Dark != dark || existing.Key.Turns != turns;
        if (needLow && !(_inflight.TryGetValue(i, out var f) && f.Key == lowKey))
        {
            Start(i, lowKey, new RenderRequest(lw, lh, QuarterTurns: turns), lowPriority);
            return; // bölge, düşük çözünürlük gelince istenir
        }
        if (!allowRegion) return;
        var vr = ViewRect(i);
        var visRect = Rect.Intersect(vr, new Rect(0, 0, _viewport.Width, _viewport.Height));
        if (visRect.IsEmpty) return;
        const int grid = 128;
        int rx = Math.Max(0, (int)((visRect.X - vr.X) * _dpi.DpiScaleX) / grid * grid - grid);
        int ry = Math.Max(0, (int)((visRect.Y - vr.Y) * _dpi.DpiScaleY) / grid * grid - grid);
        int rw = Math.Min(pw - rx, ((int)(visRect.Width * _dpi.DpiScaleX) / grid + 3) * grid);
        int rh = Math.Min(ph - ry, ((int)(visRect.Height * _dpi.DpiScaleY) / grid + 3) * grid);
        if (rw <= 0 || rh <= 0) return;
        var rkey = new RenderKey(i, doc.Revision, pw, ph, turns, dark, rx, ry, rw, rh);
        var reg = _cache.GetRegion(i);
        if (reg is not null && reg.Key.Revision == rkey.Revision && reg.Key.Width == pw && reg.Key.Dark == dark && reg.Key.Turns == turns
            && reg.Key.RX <= (int)((visRect.X - vr.X) * _dpi.DpiScaleX) && reg.Key.RY <= (int)((visRect.Y - vr.Y) * _dpi.DpiScaleY)
            && reg.Key.RX + reg.Key.RW >= (int)((visRect.Right - vr.X) * _dpi.DpiScaleX) && reg.Key.RY + reg.Key.RH >= (int)((visRect.Bottom - vr.Y) * _dpi.DpiScaleY))
            return;
        Start(i, rkey, new RenderRequest(pw, ph, rx, ry, rw, rh, turns), lowPriority);
    }

    private void Start(int page, RenderKey key, RenderRequest req, bool lowPriority)
    {
        if (_inflight.TryGetValue(page, out var cur))
        {
            if (cur.Key == key) return;
            cur.Cts.Cancel();
        }
        var doc = Document!;
        var cts = new CancellationTokenSource();
        _inflight[page] = (key, cts);
        _ = RenderAsync(doc, page, key, req, cts, lowPriority);
    }

    private async Task RenderAsync(PdfDocument doc, int page, RenderKey key, RenderRequest req, CancellationTokenSource cts, bool lowPriority)
    {
        try
        {
            var img = await doc.RenderAsync(page, req, cts.Token, lowPriority);
            var bmp = await Task.Run(() =>
            {
                if (key.Dark) ImageCodec.ApplyDarkReading(img);
                return ImageCodec.ToBitmapSource(img, 96 * _dpi.DpiScaleX);
            }, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(doc, Document) || doc.Revision != key.Revision) return;
            _cache.Put(new CacheEntry { Key = key, Bitmap = bmp });
            _errors.Remove(page);
            InvalidateVisual();
            if (!key.IsRegion) QueueRender(); // düşük çözünürlükten sonra bölge isteği için
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log.Warn($"Sayfa {page + 1} çizilemedi: {ex.Message}");
            _errors[page] = $"Sayfa {page + 1} görüntülenemedi: {ex.Message}";
            InvalidateVisual();
        }
        finally
        {
            if (_inflight.TryGetValue(page, out var cur) && ReferenceEquals(cur.Cts, cts)) _inflight.Remove(page);
            cts.Dispose();
        }
    }

    private void CancelAll()
    {
        foreach (var (_, (_, cts)) in _inflight) { try { cts.Cancel(); } catch { } }
        _inflight.Clear();
    }

    private void EnsureBasic(int page, Action? then = null)
    {
        var doc = Document;
        if (doc is null) return;
        if (_aux.TryGetValue(page, out var aux) && aux.Revision == doc.Revision && aux.Geometry is not null) { then?.Invoke(); return; }
        if (aux is null || aux.Revision != doc.Revision) { aux = new PageAux { Revision = doc.Revision }; _aux[page] = aux; }
        if (aux.LoadingBasic && then is null) return;
        aux.LoadingBasic = true;
        var rev = doc.Revision;
        _ = Load();
        async Task Load()
        {
            try
            {
                var g = await doc.GetGeometryAsync(page);
                var links = await doc.GetLinksAsync(page);
                IReadOnlyList<AnnotationInfo> notes;
                try { notes = (await doc.GetAnnotationsAsync(page)).Where(a => a.Kind == AnnotKind.Note && a.Contents.Length > 0).ToList(); }
                catch { notes = Array.Empty<AnnotationInfo>(); }
                if (doc.Revision != rev || !ReferenceEquals(doc, Document)) return;
                aux.Geometry = g; aux.Links = links; aux.Notes = notes;
                InvalidateVisual();
                then?.Invoke();
            }
            catch (Exception ex) { Log.Warn($"Sayfa {page + 1} bilgisi okunamadı: {ex.Message}"); }
            finally { aux.LoadingBasic = false; }
        }
    }

    private void EnsureText(int page, Action? then = null)
    {
        var doc = Document;
        if (doc is null) return;
        EnsureBasic(page);
        var aux = _aux[page];
        if (aux.Text is not null) { then?.Invoke(); return; }
        if (aux.LoadingText && then is null) return;
        aux.LoadingText = true;
        var rev = doc.Revision;
        _ = Load();
        async Task Load()
        {
            try
            {
                var t = await doc.GetPageTextAsync(page, lowPriority: true);
                if (doc.Revision != rev || !ReferenceEquals(doc, Document)) return;
                aux.Text = t;
                if (_aux.Count > 60) foreach (var k in _aux.Keys.Where(k => Math.Abs(k - page) > 30).ToList()) _aux.Remove(k);
                then?.Invoke();
            }
            catch (Exception ex) { Log.Warn($"Sayfa {page + 1} metni okunamadı: {ex.Message}"); }
            finally { aux.LoadingText = false; }
        }
    }

    // ─────────────── Seçim ───────────────

    public bool HasSelection => _selAnchor is not null && _selFocus is not null && _selAnchor != _selFocus;

    public void ClearSelection()
    {
        if (_selAnchor is null && _selFocus is null) return;
        _selAnchor = _selFocus = null;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tüm belgeyi seçer.</summary>
    public async Task SelectAllAsync()
    {
        var doc = Document;
        if (doc is null) return;
        int last = doc.PageCount - 1;
        var t = await doc.GetPageTextAsync(last);
        _selAnchor = new TextPosition(0, 0);
        _selFocus = new TextPosition(last, Math.Max(0, t.Length - 1));
        foreach (var p in VisiblePages()) EnsureText(p, InvalidateVisual);
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Seçili metni döndürür (sayfalar arasında satır sonu).</summary>
    public async Task<string> GetSelectedTextAsync()
    {
        var doc = Document;
        if (doc is null || _selAnchor is not { } a || _selFocus is not { } f) return "";
        var (s, e) = a.CompareTo(f) <= 0 ? (a, f) : (f, a);
        var sb = new System.Text.StringBuilder();
        for (int p = s.Page; p <= e.Page; p++)
        {
            var t = await doc.GetPageTextAsync(p);
            if (t.Length == 0) continue;
            int start = p == s.Page ? Math.Clamp(s.Char, 0, t.Length - 1) : 0;
            int end = p == e.Page ? Math.Clamp(e.Char, 0, t.Length - 1) : t.Length - 1;
            if (end < start) continue;
            if (sb.Length > 0) sb.Append("\r\n");
            sb.Append(t.Text, start, end - start + 1);
        }
        return sb.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
    }

    public async Task<bool> CopySelectionAsync()
    {
        var text = await GetSelectedTextAsync();
        if (string.IsNullOrEmpty(text)) return false;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); return true; }
            catch (System.Runtime.InteropServices.COMException) { await Task.Delay(60); }
        }
        StatusMessage?.Invoke(this, "Pano başka bir program tarafından kullanılıyor; tekrar deneyin.");
        return false;
    }

    /// <summary>Seçimi sayfa başına satır dikdörtgenlerine çevirip işaretleme isteği gönderir; seçim temizlenir.</summary>
    private async Task EmitMarkupAsync()
    {
        var doc = Document;
        if (doc is null || _selAnchor is not { } a || _selFocus is not { } f) return;
        var kind = Tool;
        var (s, e) = a.CompareTo(f) <= 0 ? (a, f) : (f, a);
        var requests = new List<MarkupRequest>();
        for (int p = s.Page; p <= e.Page; p++)
        {
            var t = await doc.GetPageTextAsync(p);
            if (t.Length == 0) continue;
            int start = p == s.Page ? Math.Clamp(s.Char, 0, t.Length - 1) : 0;
            int end = p == e.Page ? Math.Clamp(e.Char, 0, t.Length - 1) : t.Length - 1;
            if (end < start) continue;
            var rects = t.GetLineRects(start, end).Where(r => !r.IsEmpty).ToList();
            if (rects.Count > 0) requests.Add(new MarkupRequest(kind, p, rects, t.Text.Substring(start, end - start + 1)));
        }
        _selAnchor = _selFocus = null;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        foreach (var r in requests) MarkupRequested?.Invoke(this, r);
    }

    private TextPosition? HitText(Point p, bool exact)
    {
        var u = ViewToPageUnit(p);
        if (u is not { } hit || !_aux.TryGetValue(hit.Page, out var aux) || aux.Text is null || aux.Geometry is not { } g) return null;
        var (x, y) = g.FromUnit(hit.U, hit.V, ViewRotation);
        int idx = aux.Text.HitTest(x, y, exact);
        return idx < 0 ? null : new TextPosition(hit.Page, idx);
    }

    private LinkInfo? HitLink(Point p)
    {
        var u = ViewToPageUnit(p);
        if (u is not { } hit || !_aux.TryGetValue(hit.Page, out var aux) || aux.Links is null || aux.Geometry is not { } g) return null;
        if (!ViewRect(hit.Page).Contains(p)) return null;
        var (x, y) = g.FromUnit(hit.U, hit.V, ViewRotation);
        return aux.Links.FirstOrDefault(l => l.Rect.Contains(x, y));
    }

    // ─────────────── Fare / klavye ───────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        _mouseDown = p;
        if (Tool == ViewerTool.Hand || Keyboard.IsKeyDown(Key.Space))
        {
            _panning = true;
            _panStartOffset = new Point(_offset.X, _offset.Y);
            Cursor = Cursors.SizeAll;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (EditMouseDown(e, p)) { e.Handled = true; return; }
        if (Tool == ViewerTool.Note)
        {
            if (ViewToPageUnit(p) is { } nh && _aux.TryGetValue(nh.Page, out var na) && na.Geometry is { } ng && ViewRect(nh.Page).Contains(p))
            {
                var (nx, ny) = ng.FromUnit(nh.U, nh.V, ViewRotation);
                NoteRequested?.Invoke(this, (nh.Page, nx, ny));
            }
            e.Handled = true;
            return;
        }
        _linkCandidate = !MarkupTool && HitLink(p) is not null;
        if (e.ClickCount == 2) { SelectWordAt(p); e.Handled = true; return; }
        var pos = HitText(p, exact: false);
        _selAnchor = _selFocus = pos;
        _selecting = pos is not null;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    private void SelectWordAt(Point p)
    {
        var pos = HitText(p, exact: false);
        if (pos is not { } tp || !_aux.TryGetValue(tp.Page, out var aux) || aux.Text is not { } t) return;
        int s = tp.Char, e = tp.Char;
        while (s > 0 && char.IsLetterOrDigit(t.Text[s - 1])) s--;
        while (e < t.Length - 1 && char.IsLetterOrDigit(t.Text[e + 1])) e++;
        _selAnchor = new TextPosition(tp.Page, s);
        _selFocus = new TextPosition(tp.Page, e);
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_panning)
        {
            SetOffsets(_panStartOffset.X - (p.X - _mouseDown.X), _panStartOffset.Y - (p.Y - _mouseDown.Y));
            return;
        }
        if (EditMouseMove(e, p)) return;
        if (_selecting && e.LeftButton == MouseButtonState.Pressed)
        {
            if ((p - _mouseDown).Length > 3) _linkCandidate = false;
            var pos = HitText(p, exact: false);
            if (pos is not null && pos != _selFocus) { _selFocus = pos; InvalidateVisual(); }
            _autoScroll.IsEnabled = p.Y < 20 || p.Y > _viewport.Height - 20;
            return;
        }
        var link = HitLink(p);
        if (link != _hoverLink) { _hoverLink = link; InvalidateVisual(); }
        if (link is not null)
        {
            Cursor = Cursors.Hand;
            ToolTip = link.Uri ?? $"Sayfa {link.TargetPage + 1}";
        }
        else
        {
            ToolTip = null;
            Cursor = Tool == ViewerTool.Hand ? Cursors.Hand : Tool == ViewerTool.Note ? Cursors.Cross : HitText(p, exact: true) is not null ? Cursors.IBeam : Cursors.Arrow;
            if (Tool != ViewerTool.Edit && HitNote(p) is { } note) ToolTip = note;
        }
    }

    private void AutoScrollTick()
    {
        if (!_selecting || Mouse.LeftButton != MouseButtonState.Pressed) { _autoScroll.IsEnabled = false; return; }
        var p = Mouse.GetPosition(this);
        if (p.Y < 20) SetOffsets(_offset.X, _offset.Y - 30);
        else if (p.Y > _viewport.Height - 20) SetOffsets(_offset.X, _offset.Y + 30);
        foreach (var i in VisiblePages()) EnsureText(i);
        var pos = HitText(p, exact: false);
        if (pos is not null) { _selFocus = pos; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        ReleaseMouseCapture();
        _autoScroll.IsEnabled = false;
        if (_panning) { _panning = false; Cursor = Tool == ViewerTool.Hand ? Cursors.Hand : Cursors.Arrow; return; }
        if (EditMouseUp(p)) return;
        bool wasSelecting = _selecting;
        _selecting = false;
        if (_linkCandidate && (p - _mouseDown).Length <= 3 && HitLink(p) is { } link)
        {
            _linkCandidate = false;
            ClearSelection();
            LinkActivated?.Invoke(this, link);
            return;
        }
        if (wasSelecting)
        {
            if (_selAnchor == _selFocus) { _selAnchor = _selFocus = null; InvalidateVisual(); }
            else if (MarkupTool) { _ = EmitMarkupAsync(); return; }
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
            ZoomTo(Zoom * factor, e.GetPosition(this));
            e.Handled = true;
            return;
        }
        bool continuous = LayoutMode is PageLayoutMode.Continuous or PageLayoutMode.TwoPageContinuous;
        if (!continuous)
        {
            // Tek sayfa kipinde kenara gelince sonraki/önceki sayfa.
            bool atBottom = _offset.Y >= _extentH - _viewport.Height - 1;
            bool atTop = _offset.Y <= 0.5;
            if (e.Delta < 0 && atBottom) { NextPage(); e.Handled = true; return; }
            if (e.Delta > 0 && atTop && CurrentPage > 0)
            {
                PreviousPage();
                SetOffsets(_offset.X, _extentH);
                e.Handled = true; return;
            }
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) SetOffsets(_offset.X - e.Delta / 2.0, _offset.Y);
        else SetOffsets(_offset.X, _offset.Y - e.Delta * 0.6);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool continuous = LayoutMode is PageLayoutMode.Continuous or PageLayoutMode.TwoPageContinuous;
        if (EditKeyDown(e)) { e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Down: LineDown(); break;
            case Key.Up: LineUp(); break;
            case Key.Left: if (!continuous && _extentW <= _viewport.Width + 1) PreviousPage(); else LineLeft(); break;
            case Key.Right: if (!continuous && _extentW <= _viewport.Width + 1) NextPage(); else LineRight(); break;
            case Key.PageDown: if (continuous) PageDown(); else NextPage(); break;
            case Key.PageUp: if (continuous) PageUp(); else PreviousPage(); break;
            case Key.Space when !ctrl: if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) PageUp(); else PageDown(); break;
            case Key.Home when ctrl || !continuous: GoToPage(0); break;
            case Key.End when ctrl || !continuous: GoToPage(PageCount - 1); break;
            case Key.Home: SetOffsets(_offset.X, 0); break;
            case Key.End: SetOffsets(_offset.X, _extentH); break;
            case Key.C when ctrl: _ = CopySelectionAsync(); break;
            case Key.A when ctrl: _ = SelectAllAsync(); break;
            case Key.Escape: ClearSelection(); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled = true;
    }

    // ─────────────── IScrollInfo ───────────────

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; } = true;
    public double ExtentWidth => _extentW;
    public double ExtentHeight => _extentH;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    private void SetOffsets(double x, double y, bool syncPage = true)
    {
        x = Math.Clamp(x, 0, Math.Max(0, _extentW - _viewport.Width));
        y = Math.Clamp(y, 0, Math.Max(0, _extentH - _viewport.Height));
        bool changed = Math.Abs(x - _offset.X) > 0.01 || Math.Abs(y - _offset.Y) > 0.01;
        _offset = new Vector(x, y);
        if (changed)
        {
            ScrollOwner?.InvalidateScrollInfo();
            if (_editor is not null) { InvalidateMeasure(); InvalidateArrange(); }
            InvalidateVisual();
            QueueRender();
        }
        if (syncPage && !_suppressPageSync) UpdateCurrentPageFromScroll();
    }

    public void SetHorizontalOffset(double offset) => SetOffsets(offset, _offset.Y);
    public void SetVerticalOffset(double offset) => SetOffsets(_offset.X, offset);
    public void LineUp() => SetOffsets(_offset.X, _offset.Y - 48);
    public void LineDown() => SetOffsets(_offset.X, _offset.Y + 48);
    public void LineLeft() => SetOffsets(_offset.X - 48, _offset.Y);
    public void LineRight() => SetOffsets(_offset.X + 48, _offset.Y);
    public void PageUp() => SetOffsets(_offset.X, _offset.Y - _viewport.Height * 0.9);
    public void PageDown() => SetOffsets(_offset.X, _offset.Y + _viewport.Height * 0.9);
    public void PageLeft() => SetOffsets(_offset.X - _viewport.Width * 0.9, _offset.Y);
    public void PageRight() => SetOffsets(_offset.X + _viewport.Width * 0.9, _offset.Y);
    public void MouseWheelUp() => LineUp();
    public void MouseWheelDown() => LineDown();
    public void MouseWheelLeft() => LineLeft();
    public void MouseWheelRight() => LineRight();
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    /// <summary>Durum çubuğu için kısa açıklama (öz test ve tanılama).</summary>
    public string Diagnostics => $"önbellek {CacheBytes / 1048576} MB / {_cache.Count} görüntü, bekleyen {PendingRenders}, motor kuyruğu {PdfEngine.Instance.PendingCount}";
}
