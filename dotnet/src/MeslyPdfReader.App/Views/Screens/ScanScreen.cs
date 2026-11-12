using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.Core;
using MeslyPdfReader.Data;
using MeslyPdfReader.Ocr;
using MeslyPdfReader.Pdf;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MeslyPdfReader.App.Views.Screens;

/// <summary>
/// Afiş 10 — Tarama / Belge İçe Aktarma: tarayıcı, renk, çözünürlük, dosya türü; hedef (yeni PDF, açık PDF'e ekle,
/// resim olarak kaydet); otomatik kırpma, otomatik hizalama, besleyiciden çok sayfa; önizleme.
/// </summary>
public sealed class ScanScreen : UserControl
{
    private static readonly int[] DpiValues = { 150, 200, 300, 600 };
    private MainViewModel? _main;
    private AppSettings? _s;
    private IReadOnlyList<ScannerInfo> _devices = Array.Empty<ScannerInfo>();
    private readonly ComboBox _device = new() { MinWidth = 200 };
    private readonly ComboBox _color = Kit.Combo(new[] { "Renkli", "Gri tonlama", "Siyah-beyaz" }, 0);
    private readonly ComboBox _dpi = Kit.Combo(DpiValues.Select(d => d + " dpi"), 2);
    private readonly ComboBox _type = Kit.Combo(new[] { "PDF (Önerilen)", "JPG", "PNG" }, 0);
    private readonly RadioButton _tNew = new() { Content = "Yeni PDF oluştur", GroupName = "scanTarget", Margin = new Thickness(0, 4, 0, 4) };
    private readonly RadioButton _tAppend = new() { Content = "Mevcut PDF'e ekle", GroupName = "scanTarget", Margin = new Thickness(0, 4, 0, 4) };
    private readonly RadioButton _tImages = new() { Content = "Resim olarak kaydet\n(PNG/JPG)", GroupName = "scanTarget", Margin = new Thickness(0, 4, 0, 4) };
    private readonly System.Windows.Controls.CheckBox _crop = new() { Content = "Otomatik kırpma", Margin = new Thickness(0, 6, 0, 0) };
    private readonly System.Windows.Controls.CheckBox _align = new() { Content = "Sayfaları otomatik hizala (düzelt)", Margin = new Thickness(0, 6, 0, 0) };
    private readonly System.Windows.Controls.CheckBox _feeder = new() { Content = "Çok sayfalı tarama (besleyici)", Margin = new Thickness(0, 6, 0, 0) };
    private readonly System.Windows.Controls.CheckBox _ocr = new() { Content = "Metni tanı (aranabilir PDF)", Margin = new Thickness(0, 6, 0, 0) };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
    private readonly System.Windows.Controls.Button _go, _previewBtn;
    private readonly TextBlock _targetInfo = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(22, 0, 0, 4) };
    private bool _busy, _loaded;

    public ScanScreen()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star), MinWidth = 300 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.9, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });

        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = "Kaynak", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) });
        var devRow = new Grid();
        devRow.ColumnDefinitions.Add(new ColumnDefinition()); devRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        devRow.Children.Add(_device);
        var refresh = Kit.IconButton(SymbolRegular.ArrowClockwise24, "Tarayıcıları yeniden ara", () => _ = LoadDevices());
        Grid.SetColumn(refresh, 1); devRow.Children.Add(refresh);
        left.Children.Add(Kit.Field("Tarayıcı:", devRow, 96));
        left.Children.Add(Kit.Field("Renk:", _color, 96));
        left.Children.Add(Kit.Field("Çözünürlük:", _dpi, 96));
        left.Children.Add(Kit.Field("Dosya Türü:", _type, 96));
        left.Children.Add(Kit.Divider(14, 4));
        left.Children.Add(_crop); left.Children.Add(_align); left.Children.Add(_feeder); left.Children.Add(_ocr);
        _crop.ToolTip = "Kâğıdın dışında kalan, kâğıttan farklı renkteki tarayıcı zemini kırpılır.";
        _align.ToolTip = "Yan/ters sayfalar dik çevrilir, eğiklik ve kenar gölgeleri giderilir.";
        _feeder.ToolTip = "Belge besleyicili tarayıcılarda tüm kâğıtlar tek seferde taranır.";
        _ocr.ToolTip = "PDF'deki taranmış sayfaların metni bu bilgisayarda tanınır; metin seçilebilir ve aranabilir olur.";
        grid.Children.Add(Kit.Card(left, new Thickness(18)).Also(c => c.VerticalAlignment = VerticalAlignment.Top));

        var mid = new StackPanel();
        mid.Children.Add(new TextBlock { Text = "Hedef", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 6) });
        mid.Children.Add(_tNew); mid.Children.Add(_tAppend); mid.Children.Add(_targetInfo); mid.Children.Add(_tImages);
        mid.Children.Add(_status);
        var midCard = Kit.Card(mid, new Thickness(18)).Also(c => c.VerticalAlignment = VerticalAlignment.Top);
        Grid.SetColumn(midCard, 2); grid.Children.Add(midCard);

        var prevBox = new Border { Background = Brushes.White, Height = 280, Child = _preview, Padding = new Thickness(6) };
        prevBox.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
        prevBox.BorderThickness = new Thickness(1);
        var right = new StackPanel();
        right.Children.Add(new TextBlock { Text = "Önizleme", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        right.Children.Add(prevBox);
        var rightCard = Kit.Card(right, new Thickness(12)).Also(c => c.VerticalAlignment = VerticalAlignment.Top);
        Grid.SetColumn(rightCard, 4); grid.Children.Add(rightCard);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        _previewBtn = Kit.Secondary("Önizleme", null, () => _ = PreviewScan());
        _previewBtn.MinWidth = 130;
        _go = Kit.Primary("Tara ve PDF Oluştur", SymbolRegular.Scan24, () => _ = ScanAndFinish());
        _go.MinWidth = 210; _go.Margin = new Thickness(12, 0, 0, 0);
        buttons.Children.Add(_previewBtn); buttons.Children.Add(_go);

        var body = new StackPanel();
        body.Children.Add(grid);
        body.Children.Add(buttons);
        Content = Kit.Root("Tarama / Belge İçe Aktarma", new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        foreach (var rb in new[] { _tNew, _tAppend, _tImages }) rb.Checked += (_, _) => { SaveTarget(); UpdateGo(); };
        _type.SelectionChanged += (_, _) => { if (_s is not null) _s.ScanFileType = _type.SelectedIndex; if (_type.SelectedIndex > 0) _tImages.IsChecked = true; else if (_tImages.IsChecked == true) _tNew.IsChecked = true; UpdateGo(); };
        _color.SelectionChanged += (_, _) => { if (_s is not null) _s.ScanColor = _color.SelectedIndex; };
        _dpi.SelectionChanged += (_, _) => { if (_s is not null && _dpi.SelectedIndex >= 0) _s.ScanDpi = DpiValues[_dpi.SelectedIndex]; };
        _crop.Click += (_, _) => { if (_s is not null) _s.ScanAutoCrop = _crop.IsChecked == true; };
        _align.Click += (_, _) => { if (_s is not null) _s.ScanAutoCorrect = _align.IsChecked == true; };
        _feeder.Click += (_, _) => { if (_s is not null) _s.ScanFeeder = _feeder.IsChecked == true; };
        _ocr.Click += (_, _) => { if (_s is not null) _s.ScanOcr = _ocr.IsChecked == true; };
        _device.SelectionChanged += (_, _) => { if (_s is not null && _device.SelectedIndex >= 0 && _device.SelectedIndex < _devices.Count) _s.ScanDevice = _devices[_device.SelectedIndex].Id; };

        DataContextChanged += (_, _) => { _main = DataContext as MainViewModel; _s = _main?.Settings; LoadSettings(); };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true) return;
            UpdateGo();
            if (!_loaded) { _loaded = true; _ = LoadDevices(); }
        };
    }

    private void LoadSettings()
    {
        if (_s is null) return;
        _color.SelectedIndex = Math.Clamp(_s.ScanColor, 0, 2);
        _dpi.SelectedIndex = Math.Max(0, Array.IndexOf(DpiValues, _s.ScanDpi) is var i && i >= 0 ? i : 2);
        _type.SelectedIndex = _s.ScanFileType;
        _crop.IsChecked = _s.ScanAutoCrop; _align.IsChecked = _s.ScanAutoCorrect; _feeder.IsChecked = _s.ScanFeeder; _ocr.IsChecked = _s.ScanOcr;
        (_s.ScanTarget switch { 1 => _tAppend, 2 => _tImages, _ => _tNew }).IsChecked = true;
    }

    private void SaveTarget() { if (_s is not null) _s.ScanTarget = _tAppend.IsChecked == true ? 1 : _tImages.IsChecked == true ? 2 : 0; }

    private void UpdateGo()
    {
        _go.Content = _tAppend.IsChecked == true ? "Tara ve PDF'e Ekle" : _tImages.IsChecked == true ? "Tara ve Kaydet" : "Tara ve PDF Oluştur";
        _targetInfo.Text = _tAppend.IsChecked == true
            ? (_main?.ActiveTab is { } t ? $"Eklenecek belge: {t.Title} (sona)" : "Açık belge yok: tarandıktan sonra eklenecek PDF sorulur.")
            : "";
        _targetInfo.SetResourceReference(TextBlock.ForegroundProperty, "MeslyMutedTextBrush");
        _ocr.IsEnabled = _tImages.IsChecked != true;
        bool ready = !_busy && _devices.Count > 0;
        _go.IsEnabled = _previewBtn.IsEnabled = ready;
    }

    private async Task LoadDevices()
    {
        _status.Text = "Tarayıcılar aranıyor…";
        _device.Items.Clear();
        try
        {
            _devices = await WiaScanner.ListAsync();
            foreach (var d in _devices) _device.Items.Add(d.ToString());
            if (_devices.Count == 0) _status.Text = ScanException.Describe(ScanErrorKind.NoDevice);
            else
            {
                _device.SelectedIndex = Math.Max(0, _devices.ToList().FindIndex(d => d.Id == _s?.ScanDevice));
                _status.Text = $"{_devices.Count} tarayıcı bulundu.";
            }
        }
        catch (Exception ex) { _status.Text = ex is ScanException ? ex.Message : "Tarayıcılar listelenemedi: " + ex.Message; _devices = Array.Empty<ScannerInfo>(); }
        UpdateGo();
    }

    private ScanSettings Settings(int? dpiOverride = null) => new(
        _feeder.IsChecked == true ? ScanSource.Feeder : ScanSource.Flatbed,
        (ScanColorMode)Math.Max(0, _color.SelectedIndex), dpiOverride ?? DpiValues[Math.Max(0, _dpi.SelectedIndex)], null);

    private async Task<IReadOnlyList<ScannedPage>> AcquireAsync(ScanSettings s)
    {
        var dev = _devices[Math.Max(0, _device.SelectedIndex)];
        var pages = new List<ScannedPage>();
        var result = await WiaScanner.RunStaAsync(() => ScanJob.Run(new WiaScanner(), dev.Id, s, p => Dispatcher.Invoke(() => { pages.Add(p); _status.Text = $"{pages.Count}. sayfa alındı…"; })));
        if (result.StoppedBy is { } stop && pages.Count > 0) _status.Text = $"{pages.Count} sayfa tarandı, sonra durdu: {stop.Message}";
        else if (result.StoppedBy is { } stop2) throw stop2;
        return pages;
    }

    private void SetBusy(bool b, string? text)
    {
        _busy = b;
        if (text is not null) _status.Text = text;
        UpdateGo();
    }

    private async Task PreviewScan()
    {
        if (_devices.Count == 0) return;
        SetBusy(true, "Önizleme taranıyor (düşük çözünürlük)…");
        try
        {
            var pages = await AcquireAsync(Settings(75) with { Source = ScanSource.Flatbed });
            if (pages.Count == 0) { _status.Text = "Önizleme alınamadı."; return; }
            var img = ImageCodec.Decode(pages[0].Data);
            if (_crop.IsChecked == true && ImageOps.AutoCropToPaper(img) is { } c) img = c;
            _preview.Source = ImageCodec.ToBitmapSource(img);
            _status.Text = "Önizleme hazır. Ayarları değiştirip yeniden önizleyebilir ya da taramaya geçebilirsiniz.";
        }
        catch (Exception ex) { _status.Text = ex is ScanException ? ex.Message : "Önizleme başarısız: " + ex.Message; }
        finally { SetBusy(false, null); }
    }

    /// <summary>Kırpma ve hizalama (düzeltme) taranan sayfaya uygulanır.</summary>
    private ScannedPage Process(ScannedPage p, AppSettings s)
    {
        var page = p;
        if (s.ScanAutoCrop)
        {
            var img = ImageCodec.Decode(page.Data);
            if (ImageOps.AutoCropToPaper(img) is { } c)
                page = new ScannedPage { Data = page.Format == ScannedFormat.Jpeg ? ImageCodec.EncodeJpeg(c, 92, page.Dpi) : ImageCodec.EncodePng(c, page.Dpi), Format = page.Format == ScannedFormat.Jpeg ? ScannedFormat.Jpeg : ScannedFormat.Png, Dpi = page.Dpi };
        }
        if (s.ScanAutoCorrect)
            page = OcrWorkflow.CorrectScan(page, new ImageCorrection(AutoRotate: OcrEngine.Unavailable() is null, Deskew: true, CleanBorders: true), s.OcrLanguages).Page;
        return page;
    }

    private async Task ScanAndFinish()
    {
        if (_main is null || _s is null || _devices.Count == 0) return;
        var s = _s;
        SaveTarget();
        SetBusy(true, _feeder.IsChecked == true ? "Belge besleyiciden taranıyor…" : "Taranıyor…");
        IReadOnlyList<ScannedPage> pages;
        try { pages = await AcquireAsync(Settings()); }
        catch (Exception ex) { SetBusy(false, ex is ScanException ? ex.Message : "Tarama başarısız: " + ex.Message); return; }
        if (pages.Count == 0) { SetBusy(false, "Sayfa alınamadı."); return; }
        try
        {
            _status.Text = "Sayfalar hazırlanıyor…";
            var processed = await Task.Run(() => pages.Select(p => Process(p, s)).ToList());
            try { _preview.Source = ImageCodec.ToBitmapSource(ImageCodec.Decode(processed[0].Data)); } catch { }
            if (_tImages.IsChecked == true) await SaveImagesAsync(processed, s);
            else await ToPdfAsync(processed, append: _tAppend.IsChecked == true, s);
        }
        catch (Exception ex) { BusyViewModel.ShowError("Tarama", ex); }
        finally { SetBusy(false, null); }
    }

    private async Task SaveImagesAsync(IReadOnlyList<ScannedPage> pages, AppSettings s)
    {
        bool jpg = s.ScanFileType != 2;
        var first = FileDialogs.SaveFile($"Tarama_{DateTime.Now:yyyyMMdd_HHmm}{(jpg ? ".jpg" : ".png")}", jpg ? "JPEG resmi (*.jpg)|*.jpg" : "PNG resmi (*.png)|*.png", null, "Taranan resimleri kaydet");
        if (first is null) { _status.Text = "Kaydetme iptal edildi."; return; }
        var files = new List<string>();
        await Task.Run(() =>
        {
            for (int i = 0; i < pages.Count; i++)
            {
                var path = pages.Count == 1 ? first : Path.Combine(Path.GetDirectoryName(first)!, $"{Path.GetFileNameWithoutExtension(first)}_{i + 1:000}{Path.GetExtension(first)}");
                var img = ImageCodec.Decode(pages[i].Data);
                ImageCodec.Save(img, path, jpg ? ImageFormatKind.Jpeg : ImageFormatKind.Png, pages[i].Dpi, 92);
                files.Add(path);
            }
        });
        _status.Text = $"{files.Count} resim kaydedildi: {Path.GetDirectoryName(first)}";
        try { var id = _main!.Database.StartTask("scan", $"{files.Count} sayfa"); _main.Database.FinishTask(id, "ok", first, null); } catch { }
        if (DialogBase.Automation is null && MessageDialog.Confirm("Tarama tamamlandı", _status.Text + "\n\nKlasör açılsın mı?", "Klasörü aç", "Kapat", MessageKind.Success))
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{files[0]}\""); } catch { }
    }

    private async Task ToPdfAsync(IReadOnlyList<ScannedPage> pages, bool append, AppSettings s)
    {
        var main = _main!;
        DocumentViewModel? target = null;
        if (append)
        {
            target = main.ActiveTab;
            if (target is null)
            {
                var f = FileDialogs.OpenFiles(FileDialogs.PdfFilter, false, "Taranan sayfaların ekleneceği PDF");
                if (f.Length == 0) { _status.Text = "Ekleme iptal edildi; taranan sayfalar kaydedilmedi."; return; }
                await main.OpenFilesAsync(f, activate: false);
                target = main.Tabs.FirstOrDefault(t => string.Equals(t.Document.FilePath, Path.GetFullPath(f[0]), StringComparison.OrdinalIgnoreCase));
                if (target is null) return;
            }
        }
        PdfDocument? doc = null;
        string note = "";
        var ok = await main.RunTaskAsync(append ? "Taranan sayfalar ekleniyor…" : "Taranan sayfalar PDF'e dönüştürülüyor…", async (p, ct) =>
        {
            doc = await ScanPdfBuilder.BuildAsync(pages, null, ct);
            if (s.ScanOcr && OcrEngine.Unavailable() is null)
            {
                var opts = OcrRunOptions.From(s) with { Correction = ImageCorrection.None };
                note = " " + OcrWorkflow.Summarize(await OcrWorkflow.RecognizeAsync(doc, Enumerable.Range(0, doc.PageCount).ToList(), opts, p, ct));
            }
            else if (s.ScanOcr) note = " Metin tanınamadı: " + OcrEngine.Unavailable();
        }, "scan", $"{pages.Count} sayfa");
        if (!ok || doc is null) { doc?.Dispose(); return; }
        if (append && target is not null)
        {
            using (doc) await target.AppendDocumentAsync(doc, $"{pages.Count} taranmış sayfa eklendi.{note}");
            main.ShowDocumentCommand.Execute(target);
            target.CurrentPage = target.PageCount - pages.Count;
        }
        else
        {
            var vm = main.AddTab(doc, record: false);
            vm.StatusText = $"{pages.Count} taranmış sayfa.{note} Kaydetmek için Ctrl+S.";
        }
        _status.Text = $"{pages.Count} sayfa tarandı.{note}";
        main.Library.RefreshStats();
    }
}
