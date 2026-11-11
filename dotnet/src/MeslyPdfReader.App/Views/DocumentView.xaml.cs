using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeslyPdfReader.App.Controls;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.App.ViewModels;

namespace MeslyPdfReader.App.Views;

public partial class DocumentView : UserControl
{
    private const string PageDragFormat = "MeslyPdfReader.Pages";
    private DocumentViewModel? _vm;
    private Point _dragStart;
    private bool _dragArmed;
    private double _savedLeftWidth = 250;

    public DocumentView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as DocumentViewModel);
        Viewer.LinkActivated += (_, link) => _vm?.OnLinkActivated(link);
        Viewer.SelectionChanged += (_, _) => { if (_vm is not null) _vm.HasSelection = Viewer.HasSelection; };
        Viewer.StatusMessage += (_, m) => { if (_vm is not null) _vm.StatusText = m; };
        IsVisibleChanged += (_, _) => { if (IsVisible) Viewer.Focus(); };
        Loaded += (_, _) => { ApplyPanels(); Viewer.Focus(); };
    }

    private void Attach(DocumentViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (vm is null) return;
        vm.Viewer = Viewer;
        vm.PropertyChanged += OnVmChanged;
        if (App.Services.GetService(typeof(Data.AppSettings)) is Data.AppSettings s) Viewer.CacheBudgetBytes = s.RenderCacheMb * 1024L * 1024L;
        ApplyPanels();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.LeftPanel):
            case nameof(DocumentViewModel.ShowProperties):
                ApplyPanels();
                if (_vm!.LeftPanel == LeftPanelKind.Search) Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); });
                break;
            case nameof(DocumentViewModel.CurrentPage):
                if (_vm!.LeftPanel == LeftPanelKind.Thumbnails && _vm.CurrentPage < _vm.Thumbnails.Count && !ThumbList.IsKeyboardFocusWithin)
                    ThumbList.ScrollIntoView(_vm.Thumbnails[_vm.CurrentPage]);
                break;
        }
    }

    private void ApplyPanels()
    {
        if (_vm is null) return;
        bool left = _vm.LeftPanel != LeftPanelKind.None;
        if (!left && LeftCol.Width.Value > 0) _savedLeftWidth = LeftCol.Width.Value;
        LeftCol.Width = new GridLength(left ? Math.Max(180, _savedLeftWidth) : 0);
        LeftPanelHost.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitter.Visibility = left ? Visibility.Visible : Visibility.Collapsed;
        RightCol.Width = new GridLength(_vm.ShowProperties ? 280 : 0);
        RightPanel.Visibility = _vm.ShowProperties ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─────────────── Küçük resimler ───────────────

    private void ThumbList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null) return;
        _vm.SelectedPages.Clear();
        foreach (ThumbnailItem t in ThumbList.SelectedItems) _vm.SelectedPages.Add(t.Index);
        if (ThumbList.SelectedItems.Count == 1 && ThumbList.SelectedItem is ThumbnailItem one && Mouse.LeftButton == MouseButtonState.Released)
            _vm.CurrentPage = one.Index;
        else if (e.AddedItems.Count == 1 && e.AddedItems[0] is ThumbnailItem added && ThumbList.SelectedItems.Count == 1)
            _vm.CurrentPage = added.Index;
    }

    private void ThumbList_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Delete) { _vm.DeletePagesCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Enter && ThumbList.SelectedItem is ThumbnailItem t) { _vm.CurrentPage = t.Index; Viewer.Focus(); e.Handled = true; }
    }

    private void Thumb_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(ThumbList);
        _dragArmed = ItemAt(e.OriginalSource as DependencyObject) is not null;
    }

    private void Thumb_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || _vm is null) return;
        var p = e.GetPosition(ThumbList);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragArmed = false;
        var pages = _vm.SelectedPages.Count > 0 ? _vm.SelectedPages.OrderBy(i => i).ToArray() : new[] { _vm.CurrentPage };
        var data = new DataObject(PageDragFormat, pages);
        DragDrop.DoDragDrop(ThumbList, data, DragDropEffects.Move);
        DropIndicator.Visibility = Visibility.Collapsed;
    }

    private static ListBoxItem? ItemAt(DependencyObject? d)
    {
        while (d is not null and not ListBoxItem) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as ListBoxItem;
    }

    /// <summary>Bırakılan noktadaki hedef konum (bu indeksli sayfanın önüne) ve gösterge konumu.</summary>
    private (int Before, double Y) DropTarget(DragEventArgs e)
    {
        var item = ItemAt(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ThumbnailItem t || _vm is null)
            return (_vm?.PageCount ?? 0, double.NaN);
        var pos = e.GetPosition(item);
        bool after = pos.Y > item.ActualHeight / 2;
        var top = item.TranslatePoint(new Point(0, after ? item.ActualHeight : 0), ThumbList);
        return (t.Index + (after ? 1 : 0), top.Y);
    }

    private void Thumb_DragOver(object sender, DragEventArgs e)
    {
        bool pages = e.Data.GetDataPresent(PageDragFormat), files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = pages ? DragDropEffects.Move : files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        var (_, y) = DropTarget(e);
        if (double.IsNaN(y)) { DropIndicator.Visibility = Visibility.Collapsed; return; }
        DropIndicator.Width = Math.Max(40, ThumbList.ActualWidth - 24);
        Canvas.SetLeft(DropIndicator, 12);
        Canvas.SetTop(DropIndicator, y - 1.5);
        DropIndicator.Visibility = Visibility.Visible;
        // Kenarlara yaklaşınca kaydır
        var sv = FindScrollViewer(ThumbList);
        var p = e.GetPosition(ThumbList);
        if (sv is not null) { if (p.Y < 30) sv.LineUp(); else if (p.Y > ThumbList.ActualHeight - 30) sv.LineDown(); }
    }

    private void Thumb_DragLeave(object sender, DragEventArgs e) => DropIndicator.Visibility = Visibility.Collapsed;

    private async void Thumb_Drop(object sender, DragEventArgs e)
    {
        DropIndicator.Visibility = Visibility.Collapsed;
        if (_vm is null) return;
        var (before, _) = DropTarget(e);
        e.Handled = true;
        if (e.Data.GetData(PageDragFormat) is int[] pages)
        {
            await _vm.MovePagesAsync(pages, before);
            return;
        }
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var usable = files.Where(f => FileDialogs.IsPdf(f) || FileDialogs.IsImage(f)).ToList();
            if (usable.Count > 0) await _vm.InsertFilesAsync(usable, before);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer s) return s;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(d, i)) is { } r) return r;
        return null;
    }

    // ─────────────── Yer imleri ───────────────

    private void BookmarkTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_vm is not null) _vm.SelectedBookmark = e.NewValue as BookmarkNode;
    }

    private void BookmarkTree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.SelectedBookmark is { } b && b.Uri is not null) _vm.OnLinkActivated(new Core.LinkInfo(default, -1, b.Uri));
    }

    private void BookmarkTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.F2) { _vm.RenameBookmarkCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Delete) { _vm.DeleteBookmarkCommand.Execute(null); e.Handled = true; }
    }

    // ─────────────── Arama, sayfa kutusu, yakınlaştırma ───────────────

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Enter) { _vm.SearchCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { _vm.ClearSearchCommand.Execute(null); Viewer.Focus(); e.Handled = true; }
    }

    private void PageBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => PageBox.SelectAll();

    private void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Enter)
        {
            if (int.TryParse(PageBox.Text.Trim(), out var n)) _vm.PageNumber = n;
            PageBox.Text = _vm.PageNumber.ToString();
            Viewer.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape) { PageBox.Text = _vm.PageNumber.ToString(); Viewer.Focus(); e.Handled = true; }
    }

    private void ZoomCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || ZoomCombo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        switch (tag)
        {
            case "fitwidth": _vm.FitWidthCommand.Execute(null); break;
            case "fitpage": _vm.FitPageCommand.Execute(null); break;
            default: Viewer.ZoomTo(double.Parse(tag, CultureInfo.InvariantCulture) / 100.0, null); break;
        }
        Dispatcher.BeginInvoke(() => { ZoomCombo.SelectedIndex = -1; ZoomCombo.Text = _vm.ZoomPercent; Viewer.Focus(); });
    }

    private void ZoomCombo_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null || e.Key != Key.Enter) return;
        var t = ZoomCombo.Text.Replace("%", "").Replace(",", ".").Trim();
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p > 0) Viewer.ZoomTo(p / 100.0, null);
        ZoomCombo.Text = _vm.ZoomPercent;
        Viewer.Focus();
        e.Handled = true;
    }

    private void HandTool_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.Tool = _vm.Tool == ViewerTool.Hand ? ViewerTool.Select : ViewerTool.Hand;
    }
}
