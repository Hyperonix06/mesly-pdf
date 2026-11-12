using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.Convert;
using MeslyPdfReader.Core;
using MeslyPdfReader.Data;
using MeslyPdfReader.Ocr;
using MeslyPdfReader.Pdf;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MeslyPdfReader.App.ViewModels
{
    public enum OcrOutput { EditableText, SearchablePdf, Word, Text }

    /// <summary>Afiş 4 — OCR (Metin Tanıma): kaynak (PDF, resim veya tarayıcı), dil, çıktı türü, düzeltme seçenekleri.</summary>
    public sealed partial class OcrScreenViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private readonly AppSettings _s;
        public OcrScreenViewModel(MainViewModel main, AppSettings s) { _main = main; _s = s; }

        [ObservableProperty] private SourcePdf? _source;
        [ObservableProperty] private int _previewPage;
        /// <summary>Kaynağın belleğe alınmış (görüntüden/tarayıcıdan oluşturulan) belge olup olmadığı.</summary>
        public bool HasSource => Source is not null;
        public string? LastSummary { get; private set; }
        public string? LastOutput { get; private set; }

        partial void OnSourceChanged(SourcePdf? oldValue, SourcePdf? newValue)
        {
            oldValue?.Dispose();
            PreviewPage = 0;
            OnPropertyChanged(nameof(HasSource));
        }

        public static readonly string[] LangCodes = { "tur", "eng", "tur+eng" };
        public static readonly string[] LangNames = { "Türkçe", "İngilizce", "Türkçe + İngilizce" };
        public int Language { get => Math.Max(0, Array.IndexOf(LangCodes, _s.OcrLanguages)); set { _s.OcrLanguages = LangCodes[Math.Clamp(value, 0, 2)]; OnPropertyChanged(); } }
        public OcrOutput Output { get => (OcrOutput)_s.OcrOutput; set { _s.OcrOutput = (int)value; OnPropertyChanged(); } }

        public async void SetSource(string path) => await SetSourceAsync(path);

        /// <summary>PDF veya görüntü dosyası(ları) kaynak yapılır. Görüntülerden bellekte PDF oluşturulur (özgün dosyalar değişmez).</summary>
        public async Task<bool> SetSourceAsync(params string[] paths)
        {
            try
            {
                if (paths.Length == 1 && FileDialogs.IsPdf(paths[0]))
                {
                    var src = await SourcePdf.OpenAsync(_main, paths[0]);
                    if (src is null) return false;
                    Source = src;
                    return true;
                }
                var images = paths.Where(FileDialogs.IsImage).ToList();
                if (images.Count == 0) { MessageDialog.Show("Desteklenmeyen dosya", "OCR için bir PDF ya da JPG, PNG, TIFF, BMP görüntüsü seçin.", MessageKind.Warning); return false; }
                PdfDocument? doc = null;
                var ok = await _main.RunTaskAsync("Görüntüler hazırlanıyor…", async (p, ct) =>
                {
                    var sources = new List<ImagePageSource>();
                    foreach (var f in images) sources.AddRange(await Task.Run(() => ImageCodec.LoadForPdf(f), ct));
                    doc = await PdfDocument.CreateFromImagesAsync(sources, new ImagePageLayout(), p, ct);
                });
                if (!ok || doc is null) return false;
                Source = SourcePdf.FromDocument(doc, images.Count == 1 ? images[0] : Path.Combine(Path.GetDirectoryName(images[0])!, $"{images.Count} görüntü"));
                return true;
            }
            catch (Exception ex) { BusyViewModel.ShowError("Kaynak açma", ex); return false; }
        }

        public void PickFile()
        {
            var f = FileDialogs.OpenFiles("PDF ve görüntüler|*.pdf;*.jpg;*.jpeg;*.png;*.tif;*.tiff;*.bmp|" + FileDialogs.PdfFilter + "|" + FileDialogs.ImageFilter, true, "Metni tanınacak PDF veya görüntüleri seçin");
            if (f.Length > 0) _ = SetSourceAsync(f);
        }

        /// <summary>Tarayıcıdan tarayıp kaynak yapar.</summary>
        public async Task ScanAsync()
        {
            var pages = ScanDialog.Ask(_s, "Tara");
            if (pages is null) return;
            PdfDocument? doc = null;
            var ok = await _main.RunTaskAsync("Taranan sayfalar hazırlanıyor…", async (_, ct) => doc = await ScanPdfBuilder.BuildAsync(pages, null, ct));
            if (ok && doc is not null) Source = SourcePdf.FromDocument(doc, $"Taranan belge ({pages.Count} sayfa)");
        }

        public void Validate() { if (Source?.Tab is { } t && !_main.Tabs.Contains(t)) Source = null; }

        /// <summary>Geçerli sayfanın düzeltme (yön hariç: tanıma motoru gerektirir) uygulanmış önizlemesi.</summary>
        public async Task<(BgraImage Image, CorrectionReport Report)?> CorrectedPreviewAsync()
        {
            if (Source is null) return null;
            var img = await PdfTools.RenderAtDpiAsync(Source.Document, PreviewPage, 150);
            var c = new ImageCorrection(AutoRotate: _s.OcrAutoRotate && OcrEngine.Unavailable() is null, Deskew: _s.OcrDeskew, CleanBorders: _s.OcrCleanBorders, WhitenBackground: _s.OcrWhiten, Denoise: _s.OcrDenoise);
            return await Task.Run(() => OcrService.Correct(img, 150, c, _s.OcrLanguages));
        }

        /// <summary>
        /// Tanımayı başlatır. Çalışma kopyası üzerinde düzeltme ve tanıma yapılır; kaynak dosya değişmez.
        /// <paramref name="target"/>: Aranabilir PDF / Word / TXT için hedef (null → sorulur).
        /// </summary>
        public async Task<bool> StartAsync(string? target = null)
        {
            Validate();
            LastSummary = null; LastOutput = null;
            if (Source is not { } src) { MessageDialog.Show("Kaynak seçilmedi", "Önce bir PDF, görüntü seçin ya da tarayıcıdan tarayın.", MessageKind.Warning); return false; }
            if (OcrEngine.Unavailable() is { } why) { MessageDialog.Show("Metin tanıma kullanılamıyor", why, MessageKind.Error); return false; }
            var output = Output;
            if (output != OcrOutput.EditableText && target is null)
            {
                var (ext, filter) = output switch
                {
                    OcrOutput.SearchablePdf => (".pdf", FileDialogs.PdfFilter),
                    OcrOutput.Word => (".docx", "Word belgesi (*.docx)|*.docx"),
                    _ => (".txt", "Metin dosyası (*.txt)|*.txt"),
                };
                var baseName = Path.GetFileNameWithoutExtension(src.Name) + (output == OcrOutput.SearchablePdf ? " (aranabilir)" : "");
                target = FileDialogs.SaveFile(baseName + ext, filter, File.Exists(src.Path) ? Path.GetDirectoryName(src.Path) : null, "Tanınan belgeyi kaydet");
                if (target is null) return false;
                if (File.Exists(src.Path) && string.Equals(Path.GetFullPath(target), Path.GetFullPath(src.Path), StringComparison.OrdinalIgnoreCase))
                {
                    MessageDialog.Show("Başka bir ad seçin", "Özgün dosya güvende kalsın diye tanınan belge farklı bir ada kaydedilir.", MessageKind.Warning);
                    return false;
                }
            }
            var pages = Enumerable.Range(0, src.PageCount).ToList();
            var opts = OcrRunOptions.From(_s);
            IReadOnlyList<OcrPageResult>? results = null;
            IReadOnlyList<PageLayout>? layouts = null;
            PdfDocument? work = null;
            string tmp = Path.Combine(AppPaths.Temp, $"ocr-{Guid.NewGuid():N}.pdf");
            var ok = await _main.RunTaskAsync($"{src.Name}: metin tanınıyor ({pages.Count} sayfa)…", async (p, ct) =>
            {
                Directory.CreateDirectory(AppPaths.Temp);
                await src.Document.SaveCopyAsync(tmp);
                work = await PdfDocument.OpenAsync(tmp, src.Document.Password, ct);
                results = await OcrWorkflow.RecognizeAsync(work, pages, opts, new Progress<double>(v => ((IProgress<double>)p).Report(v * 0.85)), ct);
                switch (output)
                {
                    case OcrOutput.EditableText:
                        layouts = await PdfConverter.AnalyzeAsync(work, pages, new LayoutAnalyzer.Options(_s.OcrKeepLayout, false), new Progress<double>(v => ((IProgress<double>)p).Report(0.85 + v * 0.15)), ct);
                        break;
                    case OcrOutput.SearchablePdf:
                        await work.SaveAsync(target!, ct);
                        break;
                    case OcrOutput.Word:
                        await ConvertWorkflow.ConvertAsync(work, target!, new ConvertRequest(ConvertFormat.Word, pages, false, opts, _s.OcrKeepLayout, true), new Progress<double>(v => ((IProgress<double>)p).Report(0.85 + v * 0.15)), ct);
                        break;
                    case OcrOutput.Text:
                        await PdfTools.ExportTextAsync(work, target!, pages, null, ct);
                        break;
                }
            }, "ocr", src.Name, () => target);
            try
            {
                if (!ok || results is null) return false;
                LastSummary = OcrWorkflow.Summarize(results);
                LastOutput = target;
                _main.StatusText = $"OCR: {LastSummary}";
                _main.Library.RefreshStats();
                if (output == OcrOutput.EditableText && layouts is not null)
                {
                    _main.OcrEditor.Load(work!, layouts, src.Name, _s.OcrKeepLayout, LastSummary);
                    work = null; // düzenleyici küçük resimler için sahiplenir
                    _main.Navigate(AppScreen.OcrEditor);
                }
                else if (DialogBase.Automation is null && target is not null)
                {
                    if (output == OcrOutput.SearchablePdf)
                    {
                        if (MessageDialog.Confirm("Metin tanıma tamamlandı", $"{LastSummary}\n\n{target}\n\nAranabilir PDF açılsın mı?", "Aç", "Kapat", MessageKind.Success))
                            await _main.OpenFilesAsync(new[] { target });
                    }
                    else if (MessageDialog.Confirm("Metin tanıma tamamlandı", $"{LastSummary}\n\n{target}\n\nDosya açılsın mı?", "Aç", "Kapat", MessageKind.Success))
                        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
                        catch (Exception ex) { MessageDialog.Show("Dosya açılamadı", ex.Message, MessageKind.Warning); }
                }
                return true;
            }
            finally
            {
                work?.Dispose();
                if (work is not null || !ok) try { File.Delete(tmp); } catch { }
            }
        }
    }
}

namespace MeslyPdfReader.App.Views.Screens
{
    public sealed class OcrScreen : UserControl
    {
        private OcrScreenViewModel? _vm;
        private MainViewModel? _main;
        private readonly Image _preview = new() { Stretch = Stretch.Uniform };
        private readonly TextBlock _previewInfo = new() { FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _sourceName = new() { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _pageText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        private readonly ComboBox _kaynak, _lang, _output;
        private readonly System.Windows.Controls.CheckBox _layout, _deskew, _denoise;
        private readonly System.Windows.Controls.Button _start, _previewBtn;
        private readonly StackPanel _emptyHint;
        private bool _showCorrected;
        private int _renderSeq;

        public OcrScreen()
        {
            AllowDrop = true;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });

            // Sol: önizleme
            var left = new Grid();
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            left.RowDefinitions.Add(new RowDefinition());
            left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var nav = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var pager = new StackPanel { Orientation = Orientation.Horizontal };
            pager.Children.Add(Kit.IconButton(SymbolRegular.ChevronLeft24, "Önceki sayfa", () => Page(-1)));
            pager.Children.Add(_pageText);
            pager.Children.Add(Kit.IconButton(SymbolRegular.ChevronRight24, "Sonraki sayfa", () => Page(1)));
            DockPanel.SetDock(pager, Dock.Right);
            nav.Children.Add(pager);
            nav.Children.Add(_sourceName);
            left.Children.Add(nav);
            var paper = new Border { Background = Brushes.White, Padding = new Thickness(8), Child = _preview };
            paper.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.18 };
            _emptyHint = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var big = new SymbolIcon { Symbol = SymbolRegular.ScanText24, FontSize = 46, HorizontalAlignment = HorizontalAlignment.Center };
            big.SetResourceReference(ForegroundProperty, "MeslyAccentBrush");
            _emptyHint.Children.Add(big);
            _emptyHint.Children.Add(new TextBlock { Text = "Taranmış PDF veya resim seçin", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4), HorizontalAlignment = HorizontalAlignment.Center });
            _emptyHint.Children.Add(Kit.Muted("ya da dosyayı buraya sürükleyin, veya tarayıcıdan tarayın").Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
            var pickRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
            pickRow.Children.Add(Kit.Primary("Dosya Seç", SymbolRegular.FolderOpen24, () => _vm?.PickFile()));
            pickRow.Children.Add(Kit.Secondary("Tara", SymbolRegular.Scan24, () => _ = _vm?.ScanAsync()).Also(b => b.Margin = new Thickness(8, 0, 0, 0)));
            _emptyHint.Children.Add(pickRow);
            var previewHost = new Grid();
            previewHost.Children.Add(new Border { Child = paper, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top });
            previewHost.Children.Add(_emptyHint);
            var previewCard = Kit.Card(previewHost);
            previewCard.SetResourceReference(Border.BackgroundProperty, "MeslySubtleBrush");
            Grid.SetRow(previewCard, 1);
            left.Children.Add(previewCard);
            Grid.SetRow(_previewInfo, 2);
            left.Children.Add(_previewInfo);
            grid.Children.Add(left);

            // Sağ: OCR ayarları
            var right = new StackPanel();
            right.Children.Add(new TextBlock { Text = "OCR Ayarları", FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 8) });
            _kaynak = Kit.Combo(new[] { "Tarama / Resim (dosya)", "Tarayıcıdan tara", "Açık belge" }, 0);
            _kaynak.DropDownClosed += (_, _) => OnKaynak();
            right.Children.Add(Kit.Field("Kaynak:", _kaynak, 92));
            _lang = Kit.Combo(OcrScreenViewModel.LangNames, 0, i => { if (_vm is not null) _vm.Language = i; });
            right.Children.Add(Kit.Field("Dil:", _lang, 92));
            _output = Kit.Combo(new[] { "Düzenlenebilir Metin", "Aranabilir PDF", "Word (.docx)", "Metin (.txt)" }, 0, i => { if (_vm is not null) _vm.Output = (OcrOutput)i; });
            right.Children.Add(Kit.Field("Çıktı Türü:", _output, 92));
            right.Children.Add(Kit.Divider(14, 6));
            _layout = Kit.Check("Sayfa yapısını koru", true, v => { if (_main is not null) _main.Settings.OcrKeepLayout = v; }, "Başlık, paragraf, hizalama ve tablolar korunur.");
            _deskew = Kit.Check("Otomatik eğiklik düzelt", true, v => { if (_main is not null) { _main.Settings.OcrDeskew = v; RefreshPreviewIfCorrected(); } }, "Eğri taranmış satırlar yataya getirilir.");
            _denoise = Kit.Check("Gürültü azaltma (daha temiz metin)", true, v => { if (_main is not null) { _main.Settings.OcrDenoise = v; RefreshPreviewIfCorrected(); } }, "Taramadaki tek tük benekler temizlenir; noktalama korunur.");
            right.Children.Add(_layout); right.Children.Add(_deskew); right.Children.Add(_denoise);
            right.Children.Add(Kit.Muted("Tanıma tamamen bu bilgisayarda yapılır; belgeniz internete gönderilmez. Özgün dosya değişmez.", 11.5).Also(t => t.Margin = new Thickness(0, 14, 0, 0)));
            var buttons = new Grid { Margin = new Thickness(0, 22, 0, 0) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition());
            _previewBtn = Kit.Secondary("Önizleme", SymbolRegular.Eye24, () => _ = TogglePreview());
            _previewBtn.ToolTip = "Seçili düzeltmelerin (eğiklik, gürültü, kenar, yön) sayfaya etkisini gösterir";
            _start = Kit.Primary("OCR Başlat", SymbolRegular.Play24, () => _ = Start());
            Grid.SetColumn(_start, 2);
            buttons.Children.Add(_previewBtn); buttons.Children.Add(_start);
            right.Children.Add(buttons);
            var rightCard = Kit.Card(right, new Thickness(18));
            rightCard.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(rightCard, 2);
            grid.Children.Add(rightCard);

            Content = Kit.Root("OCR – Tarama ve Metin Tanıma", grid);
            DataContextChanged += (_, _) => Bind();
            IsVisibleChanged += (_, e) => { if (e.NewValue is true) { _vm?.Validate(); if (_vm is { Source: null } && _main?.ActiveTab is { } t) _vm.Source = SourcePdf.FromTab(t); Refresh(); } };
            Drop += async (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] f && _vm is not null) { e.Handled = true; await _vm.SetSourceAsync(f); } };
        }

        private void Bind()
        {
            _main = DataContext as MainViewModel;
            if (_vm is not null) _vm.PropertyChanged -= OnVm;
            _vm = _main?.OcrScreen;
            if (_vm is null) return;
            _vm.PropertyChanged += OnVm;
            Refresh();
        }

        private void OnVm(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(OcrScreenViewModel.Source)) { _showCorrected = false; Refresh(); }
            else if (e.PropertyName is nameof(OcrScreenViewModel.PreviewPage)) _ = RenderPreview();
        }

        private void OnKaynak()
        {
            if (_vm is null) return;
            switch (_kaynak.SelectedIndex)
            {
                case 0: _vm.PickFile(); break;
                case 1: _ = _vm.ScanAsync(); break;
                case 2:
                    if (_main?.ActiveTab is { } t) _vm.Source = SourcePdf.FromTab(t);
                    else MessageDialog.Show("Açık belge yok", "Şu an açık bir PDF yok. “Tarama / Resim” ile bir dosya seçin.", MessageKind.Info);
                    break;
            }
        }

        private void Refresh()
        {
            if (_vm is null || _main is null) return;
            var s = _main.Settings;
            _lang.SelectedIndex = _vm.Language;
            _output.SelectedIndex = (int)_vm.Output;
            _layout.IsChecked = s.OcrKeepLayout; _deskew.IsChecked = s.OcrDeskew; _denoise.IsChecked = s.OcrDenoise;
            bool has = _vm.HasSource;
            _emptyHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            _start.IsEnabled = _previewBtn.IsEnabled = has;
            _sourceName.Text = _vm.Source?.Name ?? "";
            _kaynak.SelectedIndex = _vm.Source?.Tab is not null ? 2 : 0;
            _ = RenderPreview();
        }

        private void Page(int d)
        {
            if (_vm?.Source is not { } s) return;
            _vm.PreviewPage = Math.Clamp(_vm.PreviewPage + d, 0, s.PageCount - 1);
        }

        private async Task TogglePreview()
        {
            _showCorrected = !_showCorrected;
            _previewBtn.Content = _showCorrected ? "Özgün görünüm" : "Önizleme";
            await RenderPreview();
        }

        private void RefreshPreviewIfCorrected() { if (_showCorrected) _ = RenderPreview(); }

        private async Task RenderPreview()
        {
            int seq = ++_renderSeq;
            if (_vm?.Source is not { } src) { _preview.Source = null; _pageText.Text = ""; _previewInfo.Text = ""; return; }
            _pageText.Text = $"{_vm.PreviewPage + 1} / {src.PageCount}";
            try
            {
                if (_showCorrected)
                {
                    _previewInfo.Text = "Düzeltmeler uygulanıyor…";
                    var r = await _vm.CorrectedPreviewAsync();
                    if (seq != _renderSeq || r is null) return;
                    _preview.Source = ImageCodec.ToBitmapSource(r.Value.Image);
                    _previewInfo.Text = "Düzeltilmiş önizleme: " + r.Value.Report.Describe() + (r.Value.Report.Note is { } n ? $" ({n})" : "");
                }
                else
                {
                    var img = await PdfTools.RenderAtDpiAsync(src.Document, _vm.PreviewPage, 110);
                    if (seq != _renderSeq) return;
                    _preview.Source = ImageCodec.ToBitmapSource(img);
                    _previewInfo.Text = src.Info;
                }
            }
            catch (Exception ex) { _previewInfo.Text = "Önizleme oluşturulamadı: " + ex.Message; }
        }

        private async Task Start()
        {
            if (_vm is null) return;
            await _vm.StartAsync();
        }
    }
}
