using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.Core;
using MeslyPdfReader.Data;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using ListView = System.Windows.Controls.ListView;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MeslyPdfReader.App.Dialogs;

public sealed class ScanPageItem
{
    public required ScannedPage Page { get; init; }
    public required ImageSource Thumb { get; init; }
    public int Number { get; set; }
}

/// <summary>Tarayıcıdan (düz cam / belge besleyici / çift taraflı) sayfa alma penceresi.</summary>
public sealed class ScanDialog : FluentWindow
{
    private readonly AppSettings _settings;
    private readonly ObservableCollection<ScanPageItem> _pages = new();
    private readonly ComboBox _device = new() { MinWidth = 240 };
    private readonly RadioButton _flat = Form.Radio("Düz cam", "src", true), _feed = Form.Radio("Belge besleyici", "src"), _duplex = Form.Radio("Belge besleyici — çift taraflı", "src");
    private readonly ComboBox _color = Form.Combo(new[] { "Renkli", "Gri tonlamalı", "Siyah-beyaz (metin)" });
    private readonly ComboBox _dpi = Form.Combo(new[] { "150 DPI (ekranda okuma)", "200 DPI", "300 DPI (önerilen, OCR için)", "400 DPI", "600 DPI (en yüksek kalite)" }, 2);
    private readonly ComboBox _paper = Form.Combo(PaperSize.Standard.Select(p => p.ToString()).Append("Tarayıcının tüm alanı"));
    private readonly CheckBox _autoFix = new() { Content = "Sayfaları otomatik düzelt", Margin = new Thickness(0, 4, 0, 0),
        ToolTip = "Yan veya ters taranan sayfayı metne bakarak dik çevirir, eğikliği düzeltir, kenar gölgelerini temizler." };
    private readonly CheckBox _ocr = new() { Content = "Metni tanı (aranabilir PDF)", Margin = new Thickness(0, 4, 0, 0),
        ToolTip = "Tarama bitince metin tanınır; PDF'de arama, kopyalama ve düzenleme yapılabilir. Tanıma bu bilgisayarda yapılır." };
    private readonly ComboBox _lang = Form.Combo(new[] { "Türkçe + İngilizce", "Türkçe", "İngilizce" });
    private static readonly string[] LangCodes = { "tur+eng", "tur", "eng" };
    private readonly List<Task> _fixing = new();
    private readonly TextBlock _status = DialogBase.Hint("");
    private readonly TextBlock _capsInfo = DialogBase.Hint("");
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 4, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _scan, _cancelScan, _finish, _driverUi;
    private readonly ListView _list;
    private readonly TextBlock _empty;
    private IReadOnlyList<ScannerInfo> _devices = Array.Empty<ScannerInfo>();
    private CancellationTokenSource? _cts;
    private bool _scanning;
    private static readonly int[] DpiValues = { 150, 200, 300, 400, 600 };

    public IReadOnlyList<ScannedPage> Result { get; private set; } = Array.Empty<ScannedPage>();

    public static IReadOnlyList<ScannedPage>? Ask(AppSettings settings, string finishText)
    {
        var d = new ScanDialog(settings, finishText);
        return d.ShowDialog() == true && d.Result.Count > 0 ? d.Result : null;
    }

    /// <summary>Öz test: modsuz pencere.</summary>
    public static ScanDialog CreateForTest(AppSettings settings) => new(settings, "PDF oluştur");
    public int DeviceCount => _devices.Count;
    public string StatusText => _status.Text;
    public Task Initialization { get; private set; } = Task.CompletedTask;

    private ScanDialog(AppSettings settings, string finishText)
    {
        _settings = settings;
        Title = "Tara";
        Width = 1000; Height = 700; MinWidth = 820; MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (Application.Current.MainWindow is { IsLoaded: true } mw && mw != this) Owner = mw;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        SetResourceReference(BackgroundProperty, "MeslyPanelBrush");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new TitleBar { Title = "Tarayıcıdan sayfa al", ShowMinimize = false });

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // ── Sol: ayarlar ──
        var left = new StackPanel { Margin = new Thickness(22, 6, 18, 16) };
        var refresh = new Button { Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowSync24 }, ToolTip = "Tarayıcıları yeniden ara", Margin = new Thickness(6, 0, 0, 0) };
        refresh.Click += async (_, _) => await LoadDevicesAsync();
        var devRow = new Grid();
        devRow.ColumnDefinitions.Add(new ColumnDefinition());
        devRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        devRow.Children.Add(_device); Grid.SetColumn(refresh, 1); devRow.Children.Add(refresh);
        left.Children.Add(Form.Labeled("Tarayıcı", devRow, 0));
        left.Children.Add(_capsInfo);
        left.Children.Add(DialogBase.Label("Kaynak", 12));
        left.Children.Add(_flat); left.Children.Add(_feed); left.Children.Add(_duplex);
        left.Children.Add(Form.Labeled("Renk", _color));
        left.Children.Add(Form.Labeled("Çözünürlük", _dpi));
        left.Children.Add(Form.Labeled("Kâğıt boyutu", _paper));
        left.Children.Add(DialogBase.Label("Tarama sonrası", 14));
        left.Children.Add(_autoFix);
        left.Children.Add(_ocr);
        left.Children.Add(Form.Labeled("Tanıma dili", _lang, 6));

        _scan = new Button { Content = "Tara", Appearance = ControlAppearance.Primary, Icon = new SymbolIcon { Symbol = SymbolRegular.ScanText24 }, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 18, 0, 0), Height = 40 };
        _scan.Click += async (_, _) => await ScanAsync();
        _cancelScan = new Button { Content = "Taramayı durdur", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        _cancelScan.Click += (_, _) => { _cts?.Cancel(); _status.Text = "Geçerli sayfa bitince durdurulacak…"; };
        _driverUi = new Button { Content = "Tarayıcının kendi penceresiyle tara…", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 8, 0, 0), ToolTip = "Sürücünün tarama penceresini açar (tek sayfa). Bazı tarayıcıların özel ayarları yalnızca burada bulunur." };
        _driverUi.Click += async (_, _) => await DriverScanAsync();
        left.Children.Add(_scan);
        left.Children.Add(_cancelScan);
        left.Children.Add(_driverUi);
        left.Children.Add(_progress);
        left.Children.Add(_status);
        body.Children.Add(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        // ── Sağ: taranan sayfalar ──
        var right = new Grid();
        right.SetResourceReference(Panel.BackgroundProperty, "MeslyCanvasBrush");
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition());
        var tools = new WrapPanel { Margin = new Thickness(14, 10, 14, 6) };
        tools.Children.Add(new TextBlock { Text = "Taranan sayfalar", FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) });
        foreach (var (text, icon, act) in new (string, SymbolRegular, Action)[]
        {
            ("Sağa döndür", SymbolRegular.ArrowRotateClockwise24, () => RotateSelected(1)),
            ("Sola döndür", SymbolRegular.ArrowRotateCounterclockwise24, () => RotateSelected(-1)),
            ("Öne", SymbolRegular.ArrowLeft24, () => MoveSelected(-1)),
            ("Arkaya", SymbolRegular.ArrowRight24, () => MoveSelected(1)),
            ("Sil", SymbolRegular.Delete24, DeleteSelected),
        })
        {
            var b = new Button { Content = text, Icon = new SymbolIcon { Symbol = icon }, Margin = new Thickness(0, 0, 6, 0), Appearance = ControlAppearance.Transparent };
            b.Click += (_, _) => act();
            tools.Children.Add(b);
        }
        right.Children.Add(tools);
        _list = new ListView { ItemsSource = _pages, SelectionMode = SelectionMode.Extended, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Margin = new Thickness(8, 0, 8, 8) };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var panel = new FrameworkElementFactory(typeof(WrapPanel));
        _list.ItemsPanel = new ItemsPanelTemplate(panel);
        _list.ItemTemplate = BuildThumbTemplate();
        _list.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Delete) DeleteSelected(); };
        Grid.SetRow(_list, 1);
        right.Children.Add(_list);
        _empty = new TextBlock { Text = "Henüz sayfa taranmadı.\nAyarları seçip “Tara”ya basın. Düz camda her sayfa için tekrar basabilirsiniz; belge besleyicideki tüm sayfalar tek seferde taranır.", TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "MeslyMutedTextBrush");
        Grid.SetRow(_empty, 1);
        right.Children.Add(_empty);
        Grid.SetColumn(right, 1);
        body.Children.Add(right);

        // ── Alt şerit ──
        var footer = new Border { Padding = new Thickness(22, 12, 22, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.SetResourceReference(Border.BackgroundProperty, "MeslyChromeBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
        var fp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "Kapat", MinWidth = 96 };
        close.Click += (_, _) => { if (ConfirmDiscard()) DialogResult = false; };
        _finish = new Button { Content = finishText, MinWidth = 150, Margin = new Thickness(8, 0, 0, 0), Appearance = ControlAppearance.Primary, IsEnabled = false };
        _finish.Click += async (_, _) =>
        {
            if (_fixing.Any(t => !t.IsCompleted))
            {
                _finish.IsEnabled = false; _status.Text = "Sayfa düzeltmeleri bitiriliyor…";
                await Task.WhenAll(_fixing.ToArray());
            }
            SaveSettings(CurrentSettings());
            Result = _pages.Select(p => p.Page).ToList();
            DialogResult = true;
        };
        fp.Children.Add(close); fp.Children.Add(_finish);
        footer.Child = fp;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;

        _pages.CollectionChanged += (_, _) => UpdatePagesUi(finishText);
        _device.SelectionChanged += async (_, _) => await LoadCapabilitiesAsync();
        Closing += (_, e) => { if (_scanning) { e.Cancel = true; _cts?.Cancel(); _status.Text = "Önce tarama durdurulmalı…"; } };
        LoadSavedSettings();
        Loaded += (_, _) => Initialization = LoadDevicesAsync();
    }

    private static DataTemplate BuildThumbTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.MarginProperty, new Thickness(6));
        border.SetValue(Border.PaddingProperty, new Thickness(6));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetResourceReference(Border.BackgroundProperty, "MeslyPanelBrush");
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        var img = new FrameworkElementFactory(typeof(System.Windows.Controls.Image));
        img.SetBinding(System.Windows.Controls.Image.SourceProperty, new System.Windows.Data.Binding(nameof(ScanPageItem.Thumb)));
        img.SetValue(FrameworkElement.WidthProperty, 140.0);
        img.SetValue(FrameworkElement.HeightProperty, 190.0);
        img.SetValue(System.Windows.Controls.Image.StretchProperty, Stretch.Uniform);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(ScanPageItem.Number)));
        label.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetValue(TextBlock.MarginProperty, new Thickness(0, 4, 0, 0));
        stack.AppendChild(img); stack.AppendChild(label);
        border.AppendChild(stack);
        return new DataTemplate { VisualTree = border };
    }

    // ─────────────── Ayarlar ───────────────

    private void LoadSavedSettings()
    {
        _color.SelectedIndex = Math.Clamp(_settings.ScanColor, 0, 2);
        _dpi.SelectedIndex = Math.Max(0, Array.IndexOf(DpiValues, _settings.ScanDpi));
        _paper.SelectedIndex = Math.Clamp(_settings.ScanPaper, 0, _paper.Items.Count - 1);
        _autoFix.IsChecked = _settings.ScanAutoCorrect;
        _ocr.IsChecked = _settings.ScanOcr;
        _lang.SelectedIndex = Math.Max(0, Array.IndexOf(LangCodes, _settings.OcrLanguages));
    }

    private ScanSettings CurrentSettings()
    {
        var src = _duplex.IsChecked == true ? ScanSource.FeederDuplex : _feed.IsChecked == true ? ScanSource.Feeder : ScanSource.Flatbed;
        var paper = _paper.SelectedIndex < PaperSize.Standard.Count ? PaperSize.Standard[_paper.SelectedIndex] : null;
        return new ScanSettings(src, (ScanColorMode)_color.SelectedIndex, DpiValues[Math.Max(0, _dpi.SelectedIndex)], paper);
    }

    private void SaveSettings(ScanSettings s)
    {
        if (_device.SelectedIndex >= 0 && _device.SelectedIndex < _devices.Count) _settings.ScanDevice = _devices[_device.SelectedIndex].Id;
        _settings.ScanSource = (int)s.Source;
        _settings.ScanColor = (int)s.Color;
        _settings.ScanDpi = s.Dpi;
        _settings.ScanPaper = _paper.SelectedIndex;
        _settings.ScanAutoCorrect = _autoFix.IsChecked == true;
        _settings.ScanOcr = _ocr.IsChecked == true;
        _settings.OcrLanguages = LangCodes[Math.Max(0, _lang.SelectedIndex)];
    }

    // ─────────────── Cihazlar ───────────────

    private async Task LoadDevicesAsync()
    {
        SetBusy(true, "Tarayıcılar aranıyor…");
        try
        {
            _devices = await WiaScanner.ListAsync();
            _device.Items.Clear();
            foreach (var d in _devices) _device.Items.Add(d.ToString());
            if (_devices.Count == 0)
            {
                _status.Text = ScanException.Describe(ScanErrorKind.NoDevice);
                _scan.IsEnabled = false;
                _capsInfo.Text = "";
                return;
            }
            int idx = Math.Max(0, _devices.ToList().FindIndex(d => d.Id == _settings.ScanDevice));
            _device.SelectedIndex = idx;
            _status.Text = $"{_devices.Count} tarayıcı bulundu.";
        }
        catch (Exception ex)
        {
            _status.Text = ex is ScanException ? ex.Message : "Tarayıcılar listelenemedi: " + ex.Message;
            _scan.IsEnabled = false;
            Log.Warn("Tarayıcı listesi: " + ex.Message);
        }
        finally { SetBusy(false, null); }
    }

    private async Task LoadCapabilitiesAsync()
    {
        if (_device.SelectedIndex < 0 || _device.SelectedIndex >= _devices.Count) return;
        var dev = _devices[_device.SelectedIndex];
        _scan.IsEnabled = false;
        try
        {
            var caps = await WiaScanner.GetCapabilitiesAsync(dev.Id);
            _flat.IsEnabled = caps.HasFlatbed;
            _feed.IsEnabled = caps.HasFeeder;
            _duplex.IsEnabled = caps.HasFeeder && caps.HasDuplex;
            var parts = new List<string>();
            if (caps.HasFlatbed) parts.Add("düz cam");
            if (caps.HasFeeder) parts.Add(caps.HasDuplex ? "çift taraflı belge besleyici" : "belge besleyici");
            _capsInfo.Text = "Desteklenen: " + string.Join(", ", parts) + (caps.HasFeeder ? caps.FeederLoaded ? " · besleyicide kâğıt var" : " · besleyici boş" : "");
            var saved = (ScanSource)_settings.ScanSource;
            var want = saved switch
            {
                ScanSource.FeederDuplex when _duplex.IsEnabled => _duplex,
                ScanSource.Feeder when _feed.IsEnabled => _feed,
                _ when caps.HasFeeder && caps.FeederLoaded => _feed,
                _ => _flat.IsEnabled ? _flat : _feed,
            };
            want.IsChecked = true;
            _scan.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            _flat.IsEnabled = true; _feed.IsEnabled = true; _duplex.IsEnabled = true;
            _scan.IsEnabled = true; // yetenek okunamasa da denemeye izin ver
        }
    }

    // ─────────────── Tarama ───────────────

    private async Task ScanAsync()
    {
        if (_device.SelectedIndex < 0 || _device.SelectedIndex >= _devices.Count) return;
        var dev = _devices[_device.SelectedIndex];
        var s = CurrentSettings();
        SaveSettings(s);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        int before = _pages.Count;
        SetBusy(true, s.Source == ScanSource.Flatbed ? "Taranıyor…" : "Belge besleyiciden taranıyor…");
        try
        {
            var result = await WiaScanner.RunStaAsync(() => ScanJob.Run(new WiaScanner(), dev.Id, s,
                page => Dispatcher.Invoke(() => { AddPage(page); _status.Text = $"{_pages.Count - before}. sayfa alındı…"; }), ct));
            int got = _pages.Count - before;
            _status.Text = result.StoppedBy is { } stop
                ? $"{got} sayfa tarandı, sonra durdu: {stop.Message}"
                : s.Source == ScanSource.Flatbed ? $"Sayfa tarandı. Sonraki sayfayı cama yerleştirip tekrar “Tara”ya basabilirsiniz." : $"Besleyiciden {got} sayfa tarandı.";
        }
        catch (ScanException ex) { _status.Text = ex.Message; Log.Warn("Tarama: " + ex.Message); }
        catch (Exception ex) { _status.Text = "Tarama başarısız: " + ex.Message; Log.Error("Tarama", ex); }
        finally { SetBusy(false, null); _cts.Dispose(); _cts = null; }
    }

    private async Task DriverScanAsync()
    {
        SetBusy(true, "Tarayıcı penceresi açılıyor…");
        try
        {
            var page = await WiaScanner.AcquireWithDriverDialogAsync();
            if (page is not null) { AddPage(page); _status.Text = "Sayfa eklendi."; }
            else _status.Text = "";
        }
        catch (ScanException ex) { _status.Text = ex.Kind == ScanErrorKind.Cancelled ? "" : ex.Message; }
        catch (Exception ex) { _status.Text = "Tarama başarısız: " + ex.Message; }
        finally { SetBusy(false, null); }
    }

    private void AddPage(ScannedPage page)
    {
        var item = new ScanPageItem { Page = page, Thumb = Thumb(page) };
        _pages.Add(item);
        if (_autoFix.IsChecked != true) return;
        // Arka planda düzelt; bitince küçük resmi ve sayfayı değiştir.
        var langs = LangCodes[Math.Max(0, _lang.SelectedIndex)];
        var c = new ImageCorrection(AutoRotate: true, Deskew: true, CleanBorders: true);
        _fixing.Add(Task.Run(() =>
        {
            try
            {
                var (fixedPage, rep) = OcrWorkflow.CorrectScan(page, c, langs);
                Dispatcher.Invoke(() =>
                {
                    int i = _pages.IndexOf(item);
                    if (i >= 0 && rep.Changed) _pages[i] = new ScanPageItem { Page = fixedPage, Thumb = Thumb(fixedPage) };
                    if (rep.Changed || rep.Note is not null) _status.Text = $"Sayfa {i + 1}: {rep.Describe()}{(rep.Note is null ? "" : " · " + rep.Note)}";
                });
            }
            catch (Exception ex) { Log.Warn("Tarama düzeltme: " + ex.Message); Dispatcher.Invoke(() => _status.Text = "Sayfa düzeltilemedi: " + ex.Message); }
        }));
    }

    private static ImageSource Thumb(ScannedPage page)
    {
        ImageSource thumb;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(page.Data);
            bi.DecodePixelWidth = 200;
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.EndInit();
            bi.Freeze();
            thumb = bi;
        }
        catch { thumb = new DrawingImage(); }
        if (page.QuarterTurns % 4 != 0 && thumb is BitmapSource bs) thumb = Rotate(bs, page.QuarterTurns);
        return thumb;
    }

    private void SetBusy(bool busy, string? text)
    {
        _scanning = busy;
        _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _cancelScan.Visibility = busy && _feed.IsChecked == true || busy && _duplex.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _scan.IsEnabled = !busy && _devices.Count > 0;
        _driverUi.IsEnabled = !busy;
        _finish.IsEnabled = !busy && _pages.Count > 0;
        if (text is not null) _status.Text = text;
    }

    // ─────────────── Sayfa düzenleme ───────────────

    private List<ScanPageItem> Selected() => _list.SelectedItems.Cast<ScanPageItem>().OrderBy(p => _pages.IndexOf(p)).ToList();

    private void RotateSelected(int turns)
    {
        foreach (var p in Selected())
        {
            p.Page.QuarterTurns = ((p.Page.QuarterTurns + turns) % 4 + 4) % 4;
            int i = _pages.IndexOf(p);
            var rotated = new ScanPageItem { Page = p.Page, Thumb = Rotate(p.Thumb, turns) };
            _pages[i] = rotated;
            _list.SelectedItems.Add(rotated);
        }
    }

    private static ImageSource Rotate(ImageSource src, int turns)
    {
        if (src is not BitmapSource bs) return src;
        var t = new TransformedBitmap(bs, new RotateTransform(turns * 90));
        t.Freeze();
        return t;
    }

    private void MoveSelected(int dir)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (dir > 0) sel.Reverse();
        foreach (var p in sel)
        {
            int i = _pages.IndexOf(p), j = i + dir;
            if (j < 0 || j >= _pages.Count) return;
            _pages.Move(i, j);
        }
    }

    private void DeleteSelected()
    {
        foreach (var p in Selected()) _pages.Remove(p);
    }

    private void UpdatePagesUi(string finishText)
    {
        for (int i = 0; i < _pages.Count; i++) _pages[i].Number = i + 1;
        _list.Items.Refresh();
        _empty.Visibility = _pages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _finish.Content = _pages.Count == 0 ? finishText : $"{finishText} ({_pages.Count} sayfa)";
        _finish.IsEnabled = !_scanning && _pages.Count > 0;
    }

    private bool ConfirmDiscard() =>
        _pages.Count == 0 || MessageDialog.Confirm("Taranan sayfalar", $"{_pages.Count} taranmış sayfa kaydedilmeden kapatılsın mı?", "Kapat", "Vazgeç");
}
