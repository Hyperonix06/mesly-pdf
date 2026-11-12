using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;

namespace MeslyPdfReader.App.Controls;

/// <summary>Düzenle aracında tıklayıp sürükleyerek eklenecek nesne türü.</summary>
public enum EditPlacement { None, Text, Image, Rectangle, Ellipse, Line, Arrow, Link, RemoveLink }

/// <summary>Düzenlenebilir bir öğe: paragraf (bir veya daha çok metin nesnesi) ya da tek bir görsel/şekil/form nesnesi.</summary>
public sealed record EditItem(int Page, RectF Bounds, int[] Indices, TextParagraph? Paragraph, PageObjectInfo? Object)
{
    public bool IsText => Paragraph is not null;
    public string KindName => Paragraph is not null ? "Metin" : Object?.KindName ?? "Nesne";
}

public sealed record EditSelection(int Page, IReadOnlyList<EditItem> Items)
{
    public EditItem? Single => Items.Count == 1 ? Items[0] : null;
    public int[] Indices => Items.SelectMany(i => i.Indices).Distinct().ToArray();
    public RectF Bounds => Items.Select(i => i.Bounds).Aggregate((a, b) => a.Union(b));
}

/// <summary>Görüntüleyicideki düzenleme hareketlerini belgeye uygulayan taraf (DocumentViewModel).</summary>
public interface IPdfEditHost
{
    void NotifyEditSelection(EditSelection? selection);
    Task<bool> MoveAsync(int page, IReadOnlyList<int> indices, double dx, double dy);
    Task<bool> ResizeAsync(int page, EditItem item, RectF target);
    Task<bool> DeleteAsync(int page, IReadOnlyList<int> indices);
    Task<bool> ReplaceTextAsync(int page, TextParagraph paragraph, string text);
    Task<bool> AddTextAsync(int page, double left, double top, double width, string text);
    /// <summary>Şekil, görsel veya bağlantı yerleştirir. (x1,y1)→(x2,y2) sürükleme yönüdür (PDF koordinatı).</summary>
    Task<bool> PlaceAsync(EditPlacement kind, int page, double x1, double y1, double x2, double y2);
    Task<bool> RemoveLinkAtAsync(int page, double x, double y);
    /// <summary>Düzenleme aracının yerinde metin kutusu için yazı tipi ailesi.</summary>
    string? UiFontFamily(TextParagraph? paragraph);
}

public sealed partial class PdfViewer
{
    public static readonly DependencyProperty EditHostProperty = DependencyProperty.Register(nameof(EditHost), typeof(IPdfEditHost), typeof(PdfViewer));
    public IPdfEditHost? EditHost { get => (IPdfEditHost?)GetValue(EditHostProperty); set => SetValue(EditHostProperty, value); }

    public static readonly DependencyProperty PlacementProperty = DependencyProperty.Register(nameof(Placement), typeof(EditPlacement), typeof(PdfViewer),
        new FrameworkPropertyMetadata(EditPlacement.None, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((PdfViewer)d).OnPlacementChanged()));
    public EditPlacement Placement { get => (EditPlacement)GetValue(PlacementProperty); set => SetValue(PlacementProperty, value); }

    private enum DragKind { None, Move, Resize, Place }
    private DragKind _drag;
    private int _handle = -1;          // 0..7: sol-üst, üst, sağ-üst, sağ, sağ-alt, alt, sol-alt, sol
    private Point _dragNow;
    private int _dragPage = -1;
    private EditSelection? _sel;
    private EditItem? _hoverItem;
    private (int Page, RectF Rect)? _reselect;
    private bool _editBusy;

    // yerinde metin düzenleyici
    private TextBox? _editor;
    private (int Page, RectF Rect, TextParagraph? Para, double FontPx)? _editorTarget;

    private static readonly Pen ItemPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0x3D, 0x7B, 0xFD))), 1));
    private static readonly Pen HoverPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0x7B, 0xFD))), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen SelPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x5E, 0xF0))), 1.5));
    private static readonly Pen PreviewPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x00))), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Brush HandleFill = Brushes.White;
    private static readonly Brush SelFill = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0x3D, 0x7B, 0xFD)));

    public EditSelection? EditSelection => _sel;
    public bool IsEditingText => _editor is not null;

    private bool EditMode => Tool == ViewerTool.Edit;

    internal void OnToolChanged()
    {
        CommitEditor(cancel: false);
        SetSelection(null);
        _hoverItem = null;
        if (!EditMode && Placement != EditPlacement.None) SetCurrentValue(PlacementProperty, EditPlacement.None);
        Cursor = Tool == ViewerTool.Hand ? Cursors.Hand : Cursors.Arrow;
        InvalidateVisual();
        QueueRender();
    }

    private void OnPlacementChanged()
    {
        if (Placement != EditPlacement.None && Tool != ViewerTool.Edit) SetCurrentValue(ToolProperty, ViewerTool.Edit);
        Cursor = Placement == EditPlacement.None ? Cursors.Arrow : Cursors.Cross;
        if (Placement != EditPlacement.None) SetSelection(null);
    }

    private void SetSelection(EditSelection? s)
    {
        if (ReferenceEquals(s, _sel)) return;
        _sel = s;
        InvalidateVisual();
        EditHost?.NotifyEditSelection(s);
    }

    /// <summary>Öz test / otomasyon: verilen noktadaki (PDF koordinatı) öğeyi seçer. Öğeler henüz yüklenmediyse false.</summary>
    public bool SelectItemAt(int page, double x, double y)
    {
        if (!_aux.TryGetValue(page, out var aux) || aux.EditItems is null) return false;
        var item = aux.EditItems.Where(i => i.Bounds.Contains(x, y)).OrderBy(i => Area(i.Bounds)).FirstOrDefault();
        SetSelection(item is null ? null : new EditSelection(page, new[] { item }));
        return item is not null;
    }

    /// <summary>Öz test: sayfanın düzenleme öğeleri yüklendi mi.</summary>
    public int EditItemCount(int page) => _aux.TryGetValue(page, out var aux) && aux.EditItems is { } l ? l.Count : -1;

    /// <summary>Yerinde düzenleyiciyi kapatır (uygulamadan).</summary>
    public void CancelTextEdit() => CommitEditor(cancel: true);

    /// <summary>Seçimi kod ile temizler (ör. panelden silme sonrası).</summary>
    public void ClearEditSelection() => SetSelection(null);

    // ─────────────── Öğeleri yükleme ───────────────

    private void EnsureEditItems(int page)
    {
        var doc = Document;
        if (doc is null) return;
        EnsureBasic(page);
        var aux = _aux[page];
        if (aux.EditItems is not null || aux.LoadingEdit) return;
        aux.LoadingEdit = true;
        int rev = doc.Revision;
        _ = Load();
        async Task Load()
        {
            try
            {
                var paras = await doc.GetParagraphsAsync(page);
                var objs = await doc.GetPageObjectsAsync(page);
                if (doc.Revision != rev || !ReferenceEquals(doc, Document)) return;
                var items = new List<EditItem>();
                foreach (var p in paras) items.Add(new EditItem(page, p.Bounds, p.ObjectIndices.ToArray(), p, null));
                foreach (var o in objs)
                {
                    if (o.Kind == PageObjectKind.Text) continue;
                    if (o.MeslyRole is PdfDocument.MarkBackground or PdfDocument.MarkUnderline) continue;
                    if (o.Bounds.Width < 0.5 && o.Bounds.Height < 0.5) continue;
                    items.Add(new EditItem(page, o.Bounds, new[] { o.Index }, null, o));
                }
                aux.EditItems = items;
                TryReselect(page, items);
                InvalidateVisual();
            }
            catch (Exception ex) { Log.Warn($"Sayfa {page + 1} nesneleri okunamadı: {ex.Message}"); }
            finally { aux.LoadingEdit = false; }
        }
    }

    private void TryReselect(int page, List<EditItem> items)
    {
        if (_reselect is not { } r || r.Page != page) return;
        _reselect = null;
        EditItem? best = null; double bestScore = 0.25;
        foreach (var it in items)
        {
            double inter = Overlap(it.Bounds, r.Rect), uni = Area(it.Bounds) + Area(r.Rect) - inter;
            double score = uni > 0 ? inter / uni : 0;
            if (score > bestScore) { bestScore = score; best = it; }
        }
        SetSelection(best is null ? null : new EditSelection(page, new[] { best }));
    }

    private static double Area(RectF r) => Math.Max(0, r.Width) * Math.Max(0, r.Height);
    private static double Overlap(RectF a, RectF b) =>
        Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left)) * Math.Max(0, Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom));

    // ─────────────── Koordinatlar ───────────────

    private (double X, double Y)? PdfPoint(int page, Point view, bool clamp = true)
    {
        if (page < 0 || page >= _pageRects.Length || !_inLayout[page] || !_aux.TryGetValue(page, out var aux) || aux.Geometry is not { } g) return null;
        var r = ViewRect(page);
        double u = (view.X - r.X) / r.Width, v = (view.Y - r.Y) / r.Height;
        if (clamp) { u = Math.Clamp(u, 0, 1); v = Math.Clamp(v, 0, 1); }
        return g.FromUnit(u, v, ViewRotation);
    }

    private int PageAt(Point p)
    {
        foreach (var i in VisiblePages()) if (ViewRect(i).Contains(p)) return i;
        return -1;
    }

    private EditItem? HitItem(Point p)
    {
        int page = PageAt(p);
        if (page < 0 || !_aux.TryGetValue(page, out var aux) || aux.EditItems is null) return null;
        if (PdfPoint(page, p) is not { } pt) return null;
        double tol = 3 / Math.Max(0.1, Zoom);
        return aux.EditItems
            .Where(i => pt.X >= i.Bounds.Left - tol && pt.X <= i.Bounds.Right + tol && pt.Y >= i.Bounds.Bottom - tol && pt.Y <= i.Bounds.Top + tol)
            .OrderBy(i => Area(i.Bounds))
            .FirstOrDefault();
    }

    private Rect? SelectionViewRect()
    {
        if (_sel is null || !TryGetViewRect(_sel.Page, _sel.Bounds, out var r)) return null;
        return r;
    }

    private static Point[] Handles(Rect r) => new[]
    {
        r.TopLeft, new Point(r.X + r.Width / 2, r.Top), r.TopRight, new Point(r.Right, r.Y + r.Height / 2),
        r.BottomRight, new Point(r.X + r.Width / 2, r.Bottom), r.BottomLeft, new Point(r.Left, r.Y + r.Height / 2),
    };

    /// <summary>Hangi tutamaçlar kullanılabilir: metin paragrafı yalnızca yatay (yeniden akış), diğerleri tümü.</summary>
    private bool HandleEnabled(int h)
    {
        var single = _sel?.Single;
        if (single is null || ViewRotation != 0) return false;
        if (single.IsText) return (h == 3 || h == 7) && single.Paragraph!.Align is TextAlign.Left or TextAlign.Center or TextAlign.Right or TextAlign.Justify;
        return single.Object?.Kind is PageObjectKind.Image or PageObjectKind.Path or PageObjectKind.Form or PageObjectKind.Shading;
    }

    private int HitHandle(Point p)
    {
        if (SelectionViewRect() is not { } r) return -1;
        var hs = Handles(r);
        for (int k = 0; k < hs.Length; k++)
            if (HandleEnabled(k) && Math.Abs(hs[k].X - p.X) <= 6 && Math.Abs(hs[k].Y - p.Y) <= 6) return k;
        return -1;
    }

    private static Cursor HandleCursor(int h) => h switch { 0 or 4 => Cursors.SizeNWSE, 2 or 6 => Cursors.SizeNESW, 1 or 5 => Cursors.SizeNS, _ => Cursors.SizeWE };

    private Rect ResizedViewRect(Rect r, Vector d)
    {
        double l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
        if (_handle is 0 or 6 or 7) l += d.X;
        if (_handle is 2 or 3 or 4) rt += d.X;
        if (_handle is 0 or 1 or 2) t += d.Y;
        if (_handle is 4 or 5 or 6) b += d.Y;
        if (rt - l < 6) rt = l + 6;
        if (b - t < 6) b = t + 6;
        return new Rect(new Point(l, t), new Point(rt, b));
    }

    // ─────────────── Çizim ───────────────

    private void DrawEditOverlay(DrawingContext dc, int page, Rect pageRect)
    {
        if (!EditMode || !_aux.TryGetValue(page, out var aux) || aux.EditItems is null) return;
        if (Placement == EditPlacement.None)
            foreach (var it in aux.EditItems)
                if (TryGetViewRect(page, it.Bounds, out var vr)) dc.DrawRectangle(null, ItemPen, Inflate(vr, 1));
        if (_hoverItem is { } h && h.Page == page && _drag == DragKind.None && TryGetViewRect(page, h.Bounds, out var hr))
            dc.DrawRectangle(null, HoverPen, Inflate(hr, 2));
        if (_sel is { } s && s.Page == page && _editor is null)
        {
            foreach (var it in s.Items)
                if (TryGetViewRect(page, it.Bounds, out var ir)) dc.DrawRectangle(SelFill, SelPen, Inflate(ir, 2));
            if (SelectionViewRect() is { } sr)
            {
                if (s.Items.Count > 1) dc.DrawRectangle(null, HoverPen, Inflate(sr, 4));
                var hs = Handles(Inflate(sr, 2));
                for (int k = 0; k < hs.Length; k++)
                    if (HandleEnabled(k)) dc.DrawRectangle(HandleFill, SelPen, new Rect(hs[k].X - 4, hs[k].Y - 4, 8, 8));
                if (_drag == DragKind.Move) dc.DrawRectangle(null, PreviewPen, Rect.Offset(Inflate(sr, 2), _dragNow - _mouseDown));
                if (_drag == DragKind.Resize) dc.DrawRectangle(null, PreviewPen, ResizedViewRect(sr, _dragNow - _mouseDown));
            }
        }
        if (_drag == DragKind.Place && _dragPage == page)
        {
            if (Placement is EditPlacement.Line or EditPlacement.Arrow) dc.DrawLine(PreviewPen, _mouseDown, _dragNow);
            else dc.DrawRectangle(SelFill, PreviewPen, new Rect(_mouseDown, _dragNow));
        }
    }

    private static Rect Inflate(Rect r, double d) { r.Inflate(d, d); return r; }

    // ─────────────── Fare ───────────────

    private bool EditMouseDown(MouseButtonEventArgs e, Point p)
    {
        if (!EditMode) return false;
        if (_editor is not null) CommitEditor(cancel: false);
        if (_editBusy) return true;
        CaptureMouse();
        _dragNow = p;
        if (Placement != EditPlacement.None)
        {
            _dragPage = PageAt(p);
            _drag = _dragPage >= 0 ? DragKind.Place : DragKind.None;
            return true;
        }
        int h = HitHandle(p);
        if (h >= 0) { _drag = DragKind.Resize; _handle = h; return true; }
        var item = HitItem(p);
        if (e.ClickCount == 2 && item?.Paragraph is { } para && item.Object is null)
        {
            ReleaseMouseCapture();
            SetSelection(new EditSelection(item.Page, new[] { item }));
            OpenEditor(item.Page, para.Bounds, para, para.Text);
            return true;
        }
        if (item is null) { SetSelection(null); _drag = DragKind.None; return true; }
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl && _sel is { } s && s.Page == item.Page)
        {
            var list = s.Items.ToList();
            if (list.Contains(item)) list.Remove(item); else list.Add(item);
            SetSelection(list.Count == 0 ? null : new EditSelection(item.Page, list));
            _drag = DragKind.None;
            return true;
        }
        if (_sel is null || !_sel.Items.Contains(item)) SetSelection(new EditSelection(item.Page, new[] { item }));
        _drag = DragKind.Move;
        _dragPage = item.Page;
        return true;
    }

    private bool EditMouseMove(MouseEventArgs e, Point p)
    {
        if (!EditMode) return false;
        if (_drag != DragKind.None && e.LeftButton == MouseButtonState.Pressed)
        {
            _dragNow = p;
            InvalidateVisual();
            return true;
        }
        if (Placement != EditPlacement.None) { Cursor = PageAt(p) >= 0 ? Cursors.Cross : Cursors.Arrow; return true; }
        int h = HitHandle(p);
        if (h >= 0) { Cursor = HandleCursor(h); return true; }
        var item = HitItem(p);
        if (!Equals(item, _hoverItem)) { _hoverItem = item; InvalidateVisual(); }
        Cursor = item is null ? Cursors.Arrow : Cursors.SizeAll;
        ToolTip = item is null ? null : item.IsText ? "Metin — taşımak için sürükleyin, düzenlemek için çift tıklayın" : $"{item.KindName} — sürükleyerek taşıyın";
        return true;
    }

    private bool EditMouseUp(Point p)
    {
        if (!EditMode) return false;
        ReleaseMouseCapture();
        var drag = _drag;
        _drag = DragKind.None;
        InvalidateVisual();
        var host = EditHost;
        if (host is null) return true;
        var moved = p - _mouseDown;
        switch (drag)
        {
            case DragKind.Move when _sel is { } s && moved.Length > 2:
                {
                    if (PdfPoint(s.Page, _mouseDown, clamp: false) is not { } a || PdfPoint(s.Page, p, clamp: false) is not { } b) break;
                    double dx = b.X - a.X, dy = b.Y - a.Y;
                    var target = s.Bounds;
                    _ = Run(() => host.MoveAsync(s.Page, s.Indices, dx, dy), s.Page, new RectF(target.Left + dx, target.Bottom + dy, target.Right + dx, target.Top + dy));
                    break;
                }
            case DragKind.Resize when _sel?.Single is { } item && SelectionViewRect() is { } sr && moved.Length > 1:
                {
                    var nr = ResizedViewRect(Inflate(sr, 2), moved);
                    nr = Inflate(nr, -2);
                    if (PdfPoint(item.Page, nr.TopLeft, clamp: false) is not { } a || PdfPoint(item.Page, nr.BottomRight, clamp: false) is not { } b) break;
                    var target = new RectF(a.X, a.Y, b.X, b.Y).Normalize();
                    _ = Run(() => host.ResizeAsync(item.Page, item, target), item.Page, target);
                    break;
                }
            case DragKind.Place when _dragPage >= 0:
                {
                    int page = _dragPage;
                    if (PdfPoint(page, _mouseDown) is not { } a || PdfPoint(page, p) is not { } b) break;
                    var kind = Placement;
                    bool click = moved.Length < 4;
                    if (kind == EditPlacement.RemoveLink) { _ = Run(() => host.RemoveLinkAtAsync(page, a.X, a.Y), -1, default); break; }
                    if (kind == EditPlacement.Text)
                    {
                        double size = 12 * 96 / 72.0 * Zoom;
                        var rect = click
                            ? new Rect(_mouseDown.X, _mouseDown.Y, Math.Max(120, 260 * Zoom), size * 1.6)
                            : new Rect(_mouseDown, p);
                        if (PdfPoint(page, rect.TopLeft) is { } tl && PdfPoint(page, rect.BottomRight) is { } br)
                            OpenEditor(page, new RectF(tl.X, br.Y, br.X, tl.Y).Normalize(), null, "");
                        SetCurrentValue(PlacementProperty, EditPlacement.None);
                        break;
                    }
                    if (click && kind is not (EditPlacement.Image)) { StatusMessage?.Invoke(this, "Eklemek için sayfada sürükleyerek bir alan çizin."); break; }
                    if (click) { b = (a.X + 200, a.Y - 150); }
                    SetCurrentValue(PlacementProperty, EditPlacement.None);
                    var ra = a; var rb = b;
                    _ = Run(() => host.PlaceAsync(kind, page, ra.X, ra.Y, rb.X, rb.Y), page, new RectF(ra.X, ra.Y, rb.X, rb.Y).Normalize());
                    break;
                }
        }
        return true;
    }

    private async Task Run(Func<Task<bool>> op, int page, RectF expected)
    {
        _editBusy = true;
        try
        {
            if (page >= 0) _reselect = (page, expected);
            bool ok = await op();
            if (!ok) _reselect = null;
        }
        catch (Exception ex) { _reselect = null; StatusMessage?.Invoke(this, ex.Message); }
        finally { _editBusy = false; InvalidateVisual(); }
    }

    private bool EditKeyDown(KeyEventArgs e)
    {
        if (!EditMode) return false;
        var host = EditHost;
        if (e.Key == Key.Escape)
        {
            if (Placement != EditPlacement.None) SetCurrentValue(PlacementProperty, EditPlacement.None);
            else SetSelection(null);
            return true;
        }
        if (_sel is not { } s || host is null || _editBusy) return false;
        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                SetSelection(null);
                _ = Run(() => host.DeleteAsync(s.Page, s.Indices), -1, default);
                return true;
            case Key.Enter or Key.F2 when s.Single?.Paragraph is { } p:
                OpenEditor(s.Page, p.Bounds, p, p.Text);
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                {
                    double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
                    var (vx, vy) = e.Key switch { Key.Left => (-step, 0.0), Key.Right => (step, 0.0), Key.Up => (0.0, -step), _ => (0.0, step) };
                    // Görünüm yönündeki oku PDF koordinatına çevir (döndürme dikkate alınır).
                    var (dx, dy) = (((ViewRotation % 4) + 4) % 4) switch { 1 => (vy, vx), 2 => (-vx, vy), 3 => (-vy, -vx), _ => (vx, -vy) };
                    var b = s.Bounds;
                    _ = Run(() => host.MoveAsync(s.Page, s.Indices, dx, dy), s.Page, new RectF(b.Left + dx, b.Bottom + dy, b.Right + dx, b.Top + dy));
                    return true;
                }
        }
        return false;
    }

    // ─────────────── Yerinde metin düzenleyici ───────────────

    /// <summary>Seçili paragraf için metin düzenleyiciyi açar (menü/panel komutu).</summary>
    public bool BeginEditSelectedText()
    {
        if (_sel?.Single?.Paragraph is not { } p) return false;
        OpenEditor(_sel.Page, p.Bounds, p, p.Text);
        return true;
    }

    private void OpenEditor(int page, RectF rect, TextParagraph? para, string text)
    {
        CommitEditor(cancel: true);
        double size = (para?.FontSize ?? 12) * 96 / 72.0 * Zoom;
        var family = EditHost?.UiFontFamily(para);
        _editor = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = Math.Max(6, size),
            FontFamily = new FontFamily(family ?? "Segoe UI"),
            FontWeight = para?.Bold == true ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = para?.Italic == true ? FontStyles.Italic : FontStyles.Normal,
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            Foreground = para is null ? Brushes.Black : new SolidColorBrush(Color.FromRgb((byte)(para.Rgb >> 16), (byte)(para.Rgb >> 8), (byte)para.Rgb)),
            BorderBrush = SelPen.Brush, BorderThickness = new Thickness(1.5),
            Padding = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ToolTip = "Ctrl+Enter: uygula · Esc: vazgeç",
        };
        _editor.TextAlignment = para?.Align switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, TextAlign.Justify => TextAlignment.Justify, _ => TextAlignment.Left };
        _editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CommitEditor(cancel: true); Focus(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CommitEditor(cancel: false); Focus(); e.Handled = true; }
        };
        _editor.LostKeyboardFocus += (_, e) => { if (_editor is not null && e.NewFocus is not null && !IsAncestorOf((DependencyObject)e.NewFocus)) CommitEditor(cancel: false); };
        _editorTarget = (page, rect, para, size);
        AddVisualChild(_editor);
        AddLogicalChild(_editor);
        InvalidateMeasure(); InvalidateArrange(); InvalidateVisual();
        Dispatcher.BeginInvoke(() => { _editor?.Focus(); _editor?.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
        StatusMessage?.Invoke(this, "Metni düzenleyin. Uygulamak için Ctrl+Enter veya dışarı tıklayın, vazgeçmek için Esc.");
    }

    private void CommitEditor(bool cancel)
    {
        if (_editor is not { } ed || _editorTarget is not { } t) return;
        _editor = null; _editorTarget = null;
        RemoveVisualChild(ed);
        RemoveLogicalChild(ed);
        InvalidateVisual();
        var host = EditHost;
        if (cancel || host is null) return;
        var text = ed.Text.Replace("\r\n", "\n").TrimEnd();
        if (t.Para is { } p)
        {
            if (text == p.Text) return;
            if (text.Length == 0) { _ = Run(() => host.DeleteAsync(t.Page, p.ObjectIndices.ToArray()), -1, default); return; }
            _ = Run(() => host.ReplaceTextAsync(t.Page, p, text), t.Page, p.Bounds);
        }
        else if (text.Length > 0)
            _ = Run(() => host.AddTextAsync(t.Page, t.Rect.Left, t.Rect.Top, Math.Max(40, t.Rect.Width), text), t.Page, t.Rect);
    }

    protected override int VisualChildrenCount => _editor is null ? 0 : 1;
    protected override Visual GetVisualChild(int index) => _editor ?? throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Düzenleyicinin görünüm dikdörtgeni (yükseklik: en az). Ölçme ve yerleştirme aynı genişliği kullanır ki yerleşim döngüsü oluşmasın.</summary>
    private Rect? EditorRect()
    {
        if (_editor is null || _editorTarget is not { } t || !TryGetViewRect(t.Page, t.Rect, out var r)) return null;
        double w = Math.Round(Math.Max(120, r.Width + t.FontPx * 1.5)), h = Math.Max(t.FontPx * 1.5, r.Height + t.FontPx * 0.8);
        return new Rect(Math.Round(r.X - 3), Math.Round(r.Y - t.FontPx * 0.25), w, h);
    }

    private void MeasureEditor()
    {
        if (_editor is null) return;
        _editor.Measure(new Size(EditorRect()?.Width ?? 200, double.PositiveInfinity));
    }

    private void ArrangeEditor()
    {
        if (_editor is null) return;
        if (EditorRect() is not { } rect) { _editor.Arrange(new Rect(-10000, -10000, 1, 1)); return; }
        rect.Height = Math.Max(rect.Height, _editor.DesiredSize.Height);
        _editor.Arrange(rect);
    }
}
