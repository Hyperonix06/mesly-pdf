using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Xps;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.Core;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using PageRange = MeslyPdfReader.Core.PageRange;
using TextSearch = MeslyPdfReader.Core.TextSearch;

namespace MeslyPdfReader.App.Dialogs;

/// <summary>Yazdırma önizlemesi ve Windows yazdırma sistemi ile yazdırma.</summary>
public sealed class PrintPreviewWindow : FluentWindow
{
    private readonly DocumentViewModel _vm;
    private readonly PrintOptions _options = new();
    private readonly ComboBox _printer = new(), _paper = new(), _scale = Form.Combo(new[] { "Sayfaya sığdır", "Yalnızca büyükse küçült", "Gerçek boyut (%100)" });
    private readonly ComboBox _orientation = Form.Combo(new[] { "Otomatik (sayfaya göre)", "Dikey", "Yatay" });
    private readonly ComboBox _color = Form.Combo(new[] { "Renkli", "Gri tonlamalı" });
    private readonly NumberBox _copies = Form.Number(1, 1, 999);
    private readonly Wpf.Ui.Controls.TextBox _range;
    private readonly CheckBox _annots = new() { Content = "Açıklamaları ve form alanlarını yazdır", IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
    private readonly System.Windows.Controls.Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly Border _paperBorder = new() { Background = Brushes.White, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.25 } };
    private readonly Canvas _paperCanvas = new();
    private readonly TextBlock _pageLabel = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 10, 0), MinWidth = 110, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _error = DialogBase.Error();
    private readonly Button _print;
    private readonly ProgressBar _progress = new() { Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private int _previewIndex;
    private PdfPaginator? _paginator;
    private readonly List<PrintQueue> _queues = new();
    private static readonly PaperSize[] Papers = PaperSize.Standard.ToArray();

    /// <summary>Öz test için modsuz önizleme penceresi.</summary>
    public static Window CreateForTest(DocumentViewModel vm) => new PrintPreviewWindow(vm);

    public static void ShowFor(DocumentViewModel vm)
    {
        var w = new PrintPreviewWindow(vm);
        w.ShowDialog();
    }

    private PrintPreviewWindow(DocumentViewModel vm)
    {
        _vm = vm;
        Title = $"Yazdır — {vm.Title}";
        Width = 1080; Height = 740; MinWidth = 820; MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (Application.Current.MainWindow is { IsLoaded: true } mw && mw != this) Owner = mw;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        SetResourceReference(BackgroundProperty, "MeslyPanelBrush");
        _range = Form.Text("tümü");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(new TitleBar { Title = Title, ShowMinimize = false });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // Sol: ayarlar
        var left = new StackPanel { Margin = new Thickness(22, 8, 18, 18) };
        LoadPrinters();
        left.Children.Add(Form.Labeled("Yazıcı", _printer, 0));
        left.Children.Add(Form.Labeled("Kopya sayısı", _copies));
        left.Children.Add(Form.Labeled($"Sayfalar (1–{vm.PageCount})", _range));
        left.Children.Add(DialogBase.Hint($"Geçerli sayfa: {vm.CurrentPage + 1}. Örnek: 1-3, 5, tek, çift"));
        foreach (var p in Papers) _paper.Items.Add(p.ToString());
        _paper.SelectedIndex = 0;
        left.Children.Add(Form.Labeled("Kâğıt", _paper));
        left.Children.Add(Form.Labeled("Ölçek", _scale));
        left.Children.Add(Form.Labeled("Yön", _orientation));
        left.Children.Add(Form.Labeled("Renk", _color));
        left.Children.Add(_annots);
        left.Children.Add(_error);
        left.Children.Add(_progress);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Kapat", MinWidth = 96, IsCancel = true };
        cancel.Click += (_, _) => Close();
        _print = new Button { Content = "Yazdır", MinWidth = 110, Appearance = ControlAppearance.Primary, Margin = new Thickness(8, 0, 0, 0), IsDefault = true, Icon = new SymbolIcon { Symbol = SymbolRegular.Print24 } };
        _print.Click += async (_, _) => await PrintAsync();
        buttons.Children.Add(cancel); buttons.Children.Add(_print);
        left.Children.Add(buttons);
        var leftScroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        body.Children.Add(leftScroll);

        // Sağ: önizleme
        var right = new Grid();
        right.SetResourceReference(Panel.BackgroundProperty, "MeslyCanvasBrush");
        right.RowDefinitions.Add(new RowDefinition());
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _paperCanvas.Children.Add(_preview);
        _paperBorder.Child = _paperCanvas;
        var viewbox = new Viewbox { Child = _paperBorder, Margin = new Thickness(28), Stretch = Stretch.Uniform };
        right.Children.Add(viewbox);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        var prev = new Button { Icon = new SymbolIcon { Symbol = SymbolRegular.ChevronLeft24 }, ToolTip = "Önceki sayfa" };
        var next = new Button { Icon = new SymbolIcon { Symbol = SymbolRegular.ChevronRight24 }, ToolTip = "Sonraki sayfa" };
        prev.Click += (_, _) => { if (_previewIndex > 0) { _previewIndex--; UpdatePreview(); } };
        next.Click += (_, _) => { if (_paginator is not null && _previewIndex < _paginator.PageCount - 1) { _previewIndex++; UpdatePreview(); } };
        nav.Children.Add(prev); nav.Children.Add(_pageLabel); nav.Children.Add(next);
        Grid.SetRow(nav, 1);
        right.Children.Add(nav);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);
        Content = root;

        foreach (var c in new[] { _paper, _scale, _orientation, _color, _printer }) c.SelectionChanged += (_, _) => Rebuild();
        _range.TextChanged += (_, _) => Rebuild();
        _annots.Checked += (_, _) => Rebuild(); _annots.Unchecked += (_, _) => Rebuild();
        Loaded += (_, _) => Rebuild();
    }

    private void LoadPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            var def = LocalPrintServer.GetDefaultPrintQueue();
            foreach (var q in server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections }))
            {
                _queues.Add(q);
                _printer.Items.Add(q.FullName);
                if (q.FullName == def?.FullName) _printer.SelectedIndex = _printer.Items.Count - 1;
            }
        }
        catch (Exception ex) { Log.Warn("Yazıcılar listelenemedi: " + ex.Message); }
        if (_printer.Items.Count == 0) _printer.Items.Add("Yazıcı bulunamadı");
        if (_printer.SelectedIndex < 0) _printer.SelectedIndex = 0;
    }

    private void Rebuild()
    {
        if (!IsLoaded) return;
        if (!PageRange.TryParse(_range.Text, _vm.PageCount, out var pages, out var err))
        {
            DialogBase.ShowError(_error, err);
            _print.IsEnabled = false;
            return;
        }
        DialogBase.ShowError(_error, _queues.Count == 0 ? "Bu bilgisayarda kullanılabilir yazıcı bulunamadı." : null);
        _print.IsEnabled = _queues.Count > 0;
        var paper = Papers[Math.Max(0, _paper.SelectedIndex)];
        _options.Pages = pages;
        _options.Paper = new Size(paper.WidthMm / 25.4 * 96, paper.HeightMm / 25.4 * 96);
        _options.Scale = (PrintScale)Math.Max(0, _scale.SelectedIndex);
        _options.Orientation = (PrintOrientation)Math.Max(0, _orientation.SelectedIndex);
        _options.Grayscale = _color.SelectedIndex == 1;
        _options.Annotations = _annots.IsChecked == true;
        _paginator = new PdfPaginator(_vm.Document, _options);
        _previewIndex = Math.Clamp(_previewIndex, 0, pages.Count - 1);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_paginator is null) return;
        try
        {
            var (paper, rect, _) = _paginator.Layout(_previewIndex);
            _paperCanvas.Width = paper.Width; _paperCanvas.Height = paper.Height;
            _preview.Source = _paginator.RenderPage(_previewIndex, 110);
            _preview.Width = rect.Width; _preview.Height = rect.Height;
            Canvas.SetLeft(_preview, rect.X); Canvas.SetTop(_preview, rect.Y);
            _pageLabel.Text = $"{_previewIndex + 1} / {_paginator.PageCount}  (sayfa {_options.Pages[_previewIndex] + 1})";
        }
        catch (Exception ex) { DialogBase.ShowError(_error, "Önizleme oluşturulamadı: " + ex.Message); }
    }

    private async Task PrintAsync()
    {
        if (_paginator is null || _queues.Count == 0) return;
        var queue = _queues[Math.Clamp(_printer.SelectedIndex, 0, _queues.Count - 1)];
        try
        {
            var ticket = queue.UserPrintTicket ?? queue.DefaultPrintTicket;
            ticket.CopyCount = (int)Form.Val(_copies);
            ticket.OutputColor = _options.Grayscale ? OutputColor.Grayscale : OutputColor.Color;
            var paper = Papers[Math.Max(0, _paper.SelectedIndex)];
            ticket.PageMediaSize = paper.Name switch
            {
                "A3" => new PageMediaSize(PageMediaSizeName.ISOA3), "A5" => new PageMediaSize(PageMediaSizeName.ISOA5),
                "Letter" => new PageMediaSize(PageMediaSizeName.NorthAmericaLetter), "Legal" => new PageMediaSize(PageMediaSizeName.NorthAmericaLegal),
                _ => new PageMediaSize(PageMediaSizeName.ISOA4),
            };
            _print.IsEnabled = false;
            _progress.Visibility = Visibility.Visible;
            _progress.IsIndeterminate = false;
            _progress.Maximum = _paginator.PageCount;
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            var tcs = new TaskCompletionSource<bool>();
            writer.WritingProgressChanged += (_, e) => _progress.Value = e.Number;
            writer.WritingCompleted += (_, e) => tcs.TrySetResult(!e.Cancelled && e.Error is null);
            writer.WritingCancelled += (_, _) => tcs.TrySetResult(false);
            writer.WriteAsync(_paginator, ticket);
            bool ok = await tcs.Task;
            _vm.StatusText = ok ? $"{_paginator.PageCount} sayfa “{queue.Name}” yazıcısına gönderildi." : "Yazdırma iptal edildi.";
            if (ok) Close();
        }
        catch (Exception ex)
        {
            Log.Error("Yazdırma", ex);
            DialogBase.ShowError(_error, "Yazdırılamadı: " + ex.Message);
        }
        finally
        {
            _print.IsEnabled = true;
            _progress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Öz test: sayfalayıcıyı bir XPS dosyasına yazar (yazıcı gerektirmez).</summary>
    public static int WriteXpsForTest(DocumentViewModel vm, string path)
    {
        var opts = new PrintOptions { Pages = Enumerable.Range(0, vm.PageCount).ToArray(), Dpi = 150 };
        var pag = new PdfPaginator(vm.Document, opts);
        using var xps = new System.Windows.Xps.Packaging.XpsDocument(path, System.IO.FileAccess.ReadWrite);
        var w = System.Windows.Xps.Packaging.XpsDocument.CreateXpsDocumentWriter(xps);
        w.Write(pag);
        return pag.PageCount;
    }
}
