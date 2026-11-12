using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace MeslyPdfReader.App.ViewModels
{
    /// <summary>Afiş 5 — OCR sonucu düzenleyicisi: tanınan metin düzenlenip Word, metin veya PDF olarak kaydedilir.</summary>
    public sealed partial class OcrEditorViewModel : ObservableObject
    {
        [ObservableProperty] private string _title = "";
        [ObservableProperty] private string _summary = "";
        [ObservableProperty] private bool _isDirty;
        public PdfDocument? Source { get; private set; }
        public IReadOnlyList<PageLayout> Layouts { get; private set; } = Array.Empty<PageLayout>();
        public bool KeepLayout { get; private set; }
        public FlowDocument? Document { get; private set; }
        public int Revision { get; private set; }
        public bool HasContent => Document is not null;

        public void Load(PdfDocument source, IReadOnlyList<PageLayout> layouts, string title, bool keepLayout, string summary)
        {
            Source?.Dispose();
            Source = source; Layouts = layouts; KeepLayout = keepLayout;
            Title = Path.GetFileNameWithoutExtension(title);
            Summary = summary;
            Document = FlowDocumentExport.Build(layouts, keepLayout);
            IsDirty = false;
            Revision++;
            OnPropertyChanged(nameof(Document));
            OnPropertyChanged(nameof(HasContent));
        }
    }
}

namespace MeslyPdfReader.App.Views.Screens
{
    public sealed class OcrEditorScreen : UserControl
    {
        private static readonly string[] FontNames = { "Segoe UI", "Calibri", "Arial", "Times New Roman", "Cambria", "Georgia", "Verdana", "Tahoma", "Consolas" };
        private static readonly double[] Sizes = { 8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 24, 28, 36, 48 };

        private OcrEditorViewModel? _vm;
        private MainViewModel? _main;
        private int _loadedRevision = -1;
        private readonly RichTextBox _rtb = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, AcceptsTab = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0), SpellCheck = { IsEnabled = false } };
        private readonly ListBox _thumbs = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        private readonly ComboBox _font = new() { IsEditable = true, Width = 150 };
        private readonly ComboBox _size = new() { IsEditable = true, Width = 64 };
        private readonly ToggleButton _bold = Toggle(SymbolRegular.TextBold24, "Kalın (Ctrl+B)");
        private readonly ToggleButton _italic = Toggle(SymbolRegular.TextItalic24, "Eğik (Ctrl+I)");
        private readonly ToggleButton _underline = Toggle(SymbolRegular.TextUnderline24, "Altı çizili (Ctrl+U)");
        private readonly ContentControl _ribbon = new();
        private readonly Dictionary<string, RadioButton> _tabs = new();
        private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Grid _empty;
        private readonly Grid _editor;
        private bool _updatingUi;
        private double _zoom = 1;

        public OcrEditorScreen()
        {
            foreach (var f in FontNames) _font.Items.Add(f);
            foreach (var s in Sizes) _size.Items.Add(s.ToString(System.Globalization.CultureInfo.CurrentCulture));
            _font.SelectionChanged += (_, _) => { if (!_updatingUi && _font.SelectedItem is string f) Apply(TextElement.FontFamilyProperty, new FontFamily(f)); };
            _font.LostFocus += (_, _) => { if (!_updatingUi && _font.Text.Length > 0) Apply(TextElement.FontFamilyProperty, new FontFamily(_font.Text)); };
            _size.SelectionChanged += (_, _) => { if (!_updatingUi && _size.SelectedItem is string s && double.TryParse(s, out var v)) Apply(TextElement.FontSizeProperty, v * 96 / 72); };
            _size.LostFocus += (_, _) => { if (!_updatingUi && double.TryParse(_size.Text, out var v) && v is >= 4 and <= 400) Apply(TextElement.FontSizeProperty, v * 96 / 72); };
            _bold.Click += (_, _) => { EditingCommands.ToggleBold.Execute(null, _rtb); _rtb.Focus(); };
            _italic.Click += (_, _) => { EditingCommands.ToggleItalic.Execute(null, _rtb); _rtb.Focus(); };
            _underline.Click += (_, _) => { EditingCommands.ToggleUnderline.Execute(null, _rtb); _rtb.Focus(); };
            _rtb.SelectionChanged += (_, _) => SyncUi();
            _rtb.TextChanged += (_, _) => { if (_vm is not null && !_updatingUi) _vm.IsDirty = true; };

            // Şerit sekmeleri
            var tabRow = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (key, text) in new[] { ("file", "Dosya"), ("edit", "Düzenle"), ("view", "Görünüm") })
            {
                var rb = new RadioButton { Content = text, GroupName = "ocrEdTabs" };
                rb.SetResourceReference(StyleProperty, "UnderlineTab");
                var k = key;
                rb.Checked += (_, _) => ShowRibbon(k);
                _tabs[key] = rb;
                tabRow.Children.Add(rb);
            }
            var ribbonCard = Kit.Card(_ribbon, new Thickness(10, 6, 10, 6));
            ribbonCard.Margin = new Thickness(0, 4, 0, 10);

            // Sol: sayfalar
            _thumbs.SelectionChanged += (_, _) => GoToPage(_thumbs.SelectedIndex);
            var left = new DockPanel { Width = 150, Margin = new Thickness(0, 0, 12, 0) };
            var pagesTitle = new TextBlock { Text = "Sayfalar", FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 8) };
            DockPanel.SetDock(pagesTitle, Dock.Top);
            left.Children.Add(pagesTitle);
            left.Children.Add(new ScrollViewer { Content = _thumbs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            // Sağ: sayfa görünümlü düzenleyici
            var page = new Border { Background = Brushes.White, MaxWidth = 820, Padding = new Thickness(56, 48, 56, 48), Child = _rtb, Margin = new Thickness(0, 0, 0, 12) };
            page.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Opacity = 0.16 };
            _rtb.Foreground = Brushes.Black;
            var canvas = new Border { Child = page, Padding = new Thickness(20) };
            canvas.SetResourceReference(Border.BackgroundProperty, "MeslyCanvasBrush");
            var center = new Grid();
            center.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            center.ColumnDefinitions.Add(new ColumnDefinition());
            center.Children.Add(left);
            Grid.SetColumn(canvas, 1);
            center.Children.Add(canvas);

            _editor = new Grid();
            _editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _editor.RowDefinitions.Add(new RowDefinition());
            _editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _editor.Children.Add(tabRow);
            Grid.SetRow(ribbonCard, 1); _editor.Children.Add(ribbonCard);
            Grid.SetRow(center, 2); _editor.Children.Add(center);
            Grid.SetRow(_status, 3); _status.Margin = new Thickness(2, 8, 0, 0); _editor.Children.Add(_status);

            _empty = new Grid();
            var es = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            es.Children.Add(new TextBlock { Text = "Henüz tanınan metin yok", FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            es.Children.Add(Kit.Muted("OCR ekranında “Düzenlenebilir Metin” çıktısıyla tanıma yapın; sonuç burada düzenlenir.").Also(t => { t.Margin = new Thickness(0, 6, 0, 12); t.HorizontalAlignment = HorizontalAlignment.Center; }));
            es.Children.Add(Kit.Primary("OCR ekranına git", SymbolRegular.ScanText24, () => _main?.Navigate(AppScreen.Ocr)).Also(b => b.HorizontalAlignment = HorizontalAlignment.Center));
            _empty.Children.Add(es);

            var host = new Grid();
            host.Children.Add(_editor);
            host.Children.Add(_empty);
            Content = Kit.Root("OCR Sonucu Düzenleme", host);
            _tabs["edit"].IsChecked = true;

            DataContextChanged += (_, _) => Bind();
            IsVisibleChanged += (_, e) => { if (e.NewValue is true) LoadIfChanged(); };
            _rtb.PreviewKeyDown += (_, e) =>
            {
                if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { _ = SaveDocx(); e.Handled = true; }
            };
        }

        private static ToggleButton Toggle(SymbolRegular icon, string tip)
        {
            var b = new ToggleButton { Content = new SymbolIcon { Symbol = icon, FontSize = 16 }, ToolTip = tip, Width = 32, Height = 32 };
            b.SetResourceReference(StyleProperty, "ToolButtonStyle");
            return b;
        }

        private void Bind()
        {
            _main = DataContext as MainViewModel;
            _vm = _main?.OcrEditor;
            LoadIfChanged();
        }

        private void LoadIfChanged()
        {
            if (_vm is null) return;
            bool has = _vm.HasContent;
            _empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            _editor.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (!has || _loadedRevision == _vm.Revision) return;
            _loadedRevision = _vm.Revision;
            _updatingUi = true;
            _rtb.Document = _vm.Document!;
            _rtb.CaretPosition = _rtb.Document.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            _updatingUi = false;
            SyncUi();
            _vm.IsDirty = false;
            _status.Text = _vm.Summary;
            _ = LoadThumbs();
        }

        private async Task LoadThumbs()
        {
            _thumbs.Items.Clear();
            if (_vm?.Source is not { } src) return;
            foreach (var l in _vm.Layouts)
            {
                var img = new Image { Width = 110, Stretch = Stretch.Uniform };
                var sp = new StackPanel { Margin = new Thickness(4) };
                var b = new Border { Background = Brushes.White, BorderThickness = new Thickness(1), Child = img, HorizontalAlignment = HorizontalAlignment.Center };
                b.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
                sp.Children.Add(b);
                sp.Children.Add(new TextBlock { Text = (l.PageIndex + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
                _thumbs.Items.Add(new ListBoxItem { Content = sp, Tag = l.PageIndex });
                try { img.Source = ImageCodec.ToBitmapSource(await PdfTools.RenderAtDpiAsync(src, l.PageIndex, 24)); } catch { }
            }
        }

        private void GoToPage(int index)
        {
            if (index < 0 || _thumbs.Items[index] is not ListBoxItem { Tag: int page }) return;
            var block = _rtb.Document.Blocks.FirstOrDefault(b => b.Tag is int p && p == page);
            block?.BringIntoView();
            if (block is not null) _rtb.CaretPosition = block.ContentStart;
        }

        // ─────────────── Biçim ───────────────

        private void Apply(DependencyProperty p, object value)
        {
            if (_rtb.Selection.IsEmpty)
            {
                // Seçim yoksa imlecin bulunduğu sözcüğe değil, yazılacak metne uygulanır.
                _rtb.Selection.ApplyPropertyValue(p, value);
            }
            else _rtb.Selection.ApplyPropertyValue(p, value);
            if (_vm is not null) _vm.IsDirty = true;
            _rtb.Focus();
        }

        private void SyncUi()
        {
            _updatingUi = true;
            try
            {
                var sel = _rtb.Selection;
                var ff = sel.GetPropertyValue(TextElement.FontFamilyProperty);
                _font.Text = ff is FontFamily f ? f.Source.Split(',')[0] : "";
                var fs = sel.GetPropertyValue(TextElement.FontSizeProperty);
                _size.Text = fs is double d ? Math.Round(d * 72 / 96, 1).ToString(System.Globalization.CultureInfo.CurrentCulture) : "";
                _bold.IsChecked = sel.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight w && w.ToOpenTypeWeight() >= 600;
                _italic.IsChecked = sel.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle st && st == FontStyles.Italic;
                _underline.IsChecked = sel.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection td && td.Any(x => x.Location == TextDecorationLocation.Underline);
            }
            finally { _updatingUi = false; }
        }

        private UIElement ColorButton()
        {
            var b = Kit.IconButton(SymbolRegular.TextColor24, "Yazı rengi", () => { });
            var menu = new ContextMenu();
            foreach (var (name, hex) in new[] { ("Siyah", "#000000"), ("Kırmızı", "#D91315"), ("Mavi", "#1A56DB"), ("Yeşil", "#15803D"), ("Turuncu", "#C2410C"), ("Gri", "#6B7280") })
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var mi = new System.Windows.Controls.MenuItem { Header = name, Icon = new Border { Width = 14, Height = 14, Background = new SolidColorBrush(c), CornerRadius = new CornerRadius(3) } };
                mi.Click += (_, _) => Apply(TextElement.ForegroundProperty, new SolidColorBrush(c));
                menu.Items.Add(mi);
            }
            b.ContextMenu = menu;
            b.Click += (_, _) => { menu.PlacementTarget = b; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; };
            return b;
        }

        private static Border Sep() => new Border { Width = 1, Height = 24, Margin = new Thickness(8, 0, 8, 0) }.WithResource(Border.BackgroundProperty, "MeslyBorderBrush");

        private void ShowRibbon(string key)
        {
            var row = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            void Add(UIElement e) => row.Children.Add(e);
            switch (key)
            {
                case "file":
                    Add(Kit.Secondary("Word olarak kaydet", SymbolRegular.DocumentText24, () => _ = SaveDocx()).Also(b => b.Margin = new Thickness(0, 0, 8, 0)));
                    Add(Kit.Secondary("Metin olarak kaydet", SymbolRegular.TextT24, SaveTxt).Also(b => b.Margin = new Thickness(0, 0, 8, 0)));
                    Add(Kit.Secondary("PDF olarak kaydet", SymbolRegular.DocumentPdf24, SavePdf).Also(b => b.Margin = new Thickness(0, 0, 8, 0)));
                    Add(Kit.Secondary("Tümünü kopyala", SymbolRegular.Copy24, CopyAll).Also(b => b.Margin = new Thickness(0, 0, 8, 0)));
                    Add(Kit.Secondary("Yazdır", SymbolRegular.Print24, Print));
                    break;
                case "view":
                    Add(Kit.IconButton(SymbolRegular.ZoomOut24, "Uzaklaştır", () => Zoom(-0.1)));
                    Add(Kit.IconButton(SymbolRegular.ZoomIn24, "Yakınlaştır", () => Zoom(0.1)));
                    Add(Kit.Secondary("%100", null, () => Zoom(1 - _zoom)).Also(b => b.Margin = new Thickness(6, 0, 8, 0)));
                    Add(Sep());
                    Add(Kit.Secondary("Sayfa listesini göster/gizle", SymbolRegular.PanelLeft24, () => { var l = (FrameworkElement)_thumbs.Parent; var d = (FrameworkElement)((FrameworkElement)l).Parent; d.Visibility = d.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; }));
                    Add(Sep());
                    Add(Kit.Secondary("Özgün PDF'yi aç", SymbolRegular.Open24, OpenSource));
                    break;
                default:
                    Add(Kit.IconButton(SymbolRegular.ArrowUndo24, "Geri al (Ctrl+Z)", () => { ApplicationCommands.Undo.Execute(null, _rtb); _rtb.Focus(); }));
                    Add(Kit.IconButton(SymbolRegular.ArrowRedo24, "Yinele (Ctrl+Y)", () => { ApplicationCommands.Redo.Execute(null, _rtb); _rtb.Focus(); }));
                    Add(Sep());
                    Add(_font.Also(f => f.Margin = new Thickness(0, 0, 6, 0)));
                    Add(_size);
                    Add(Sep());
                    Add(_bold); Add(_italic); Add(_underline); Add(ColorButton());
                    Add(Sep());
                    Add(Kit.IconButton(SymbolRegular.TextAlignLeft24, "Sola hizala", () => { EditingCommands.AlignLeft.Execute(null, _rtb); _rtb.Focus(); }));
                    Add(Kit.IconButton(SymbolRegular.TextAlignCenter24, "Ortala", () => { EditingCommands.AlignCenter.Execute(null, _rtb); _rtb.Focus(); }));
                    Add(Kit.IconButton(SymbolRegular.TextAlignRight24, "Sağa hizala", () => { EditingCommands.AlignRight.Execute(null, _rtb); _rtb.Focus(); }));
                    Add(Kit.IconButton(SymbolRegular.TextAlignJustify24, "İki yana yasla", () => { EditingCommands.AlignJustify.Execute(null, _rtb); _rtb.Focus(); }));
                    Add(Sep());
                    Add(Kit.IconButton(SymbolRegular.TextBulletListLtr24, "Madde işaretli liste", () => { EditingCommands.ToggleBullets.Execute(null, _rtb); _rtb.Focus(); }));
                    Add(Kit.IconButton(SymbolRegular.TextNumberListLtr24, "Numaralı liste", () => { EditingCommands.ToggleNumbering.Execute(null, _rtb); _rtb.Focus(); }));
                    Add(Kit.IconButton(SymbolRegular.Link24, "Seçili metne bağlantı ekle", AddLink));
                    break;
            }
            _ribbon.Content = row;
        }

        private void Zoom(double d)
        {
            _zoom = Math.Clamp(_zoom + d, 0.5, 2.5);
            _rtb.LayoutTransform = new ScaleTransform(_zoom, _zoom);
            _status.Text = $"Yakınlaştırma %{_zoom * 100:0}";
        }

        private void AddLink()
        {
            if (_rtb.Selection.IsEmpty) { _status.Text = "Bağlantı eklemek için önce metni seçin."; return; }
            var url = LinkDialog.Ask();
            if (url is null) return;
            var range = new TextRange(_rtb.Selection.Start, _rtb.Selection.End);
            try
            {
                var link = new Hyperlink(range.Start, range.End) { NavigateUri = new Uri(url) };
                link.RequestNavigate += (_, e) => { try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { } };
                if (_vm is not null) _vm.IsDirty = true;
            }
            catch (Exception ex) { _status.Text = "Bağlantı eklenemedi: " + ex.Message; }
        }

        // ─────────────── Kaydetme ───────────────

        private string BaseName => string.IsNullOrWhiteSpace(_vm?.Title) ? "Tanınan metin" : _vm!.Title;

        public async Task<string?> SaveDocx(string? target = null)
        {
            if (_vm?.Document is null) return null;
            target ??= FileDialogs.SaveFile(BaseName + ".docx", "Word belgesi (*.docx)|*.docx", null, "Word belgesi olarak kaydet");
            if (target is null) return null;
            try
            {
                var doc = _rtb.Document;
                await Task.Yield();
                FlowDocumentExport.SaveDocx(doc, target, BaseName);
                _vm.IsDirty = false;
                _status.Text = $"Kaydedildi: {target}";
                if (DialogBase.Automation is null && MessageDialog.Confirm("Kaydedildi", $"{target}\n\nDosya açılsın mı?", "Aç", "Kapat", MessageKind.Success))
                    try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch (Exception ex) { MessageDialog.Show("Açılamadı", ex.Message, MessageKind.Warning); }
                return target;
            }
            catch (Exception ex) { BusyViewModel.ShowError("Word olarak kaydetme", ex); return null; }
        }

        public string? SaveTxt(string? target)
        {
            if (_vm?.Document is null) return null;
            target ??= FileDialogs.SaveFile(BaseName + ".txt", "Metin dosyası (*.txt)|*.txt", null, "Metin olarak kaydet");
            if (target is null) return null;
            try { FlowDocumentExport.SaveText(_rtb.Document, target); _vm.IsDirty = false; _status.Text = $"Kaydedildi: {target}"; return target; }
            catch (Exception ex) { BusyViewModel.ShowError("Metin olarak kaydetme", ex); return null; }
        }

        private void SaveTxt() => SaveTxt(null);

        private void SavePdf()
        {
            if (_vm?.Document is null) return;
            if (!FlowDocumentExport.PdfPrinterAvailable())
            {
                MessageDialog.Show("PDF yazıcısı bulunamadı", "Windows'un “Microsoft Print to PDF” yazıcısı bu bilgisayarda kapalı. Denetim Masası › Programlar › Windows özelliklerini aç/kapat bölümünden etkinleştirebilir ya da metni Word olarak kaydedip Word'den PDF'ye aktarabilirsiniz.", MessageKind.Warning);
                return;
            }
            try
            {
                if (FlowDocumentExport.PrintToPdf(_rtb.Document, BaseName)) _status.Text = "PDF, Windows'un PDF yazıcısına gönderildi; dosya adını açılan Windows penceresinde seçin.";
            }
            catch (Exception ex) { BusyViewModel.ShowError("PDF olarak kaydetme", ex); }
        }

        private void CopyAll()
        {
            try { Clipboard.SetText(FlowDocumentExport.PlainText(_rtb.Document)); _status.Text = "Tüm metin panoya kopyalandı."; }
            catch (Exception ex) { _status.Text = "Kopyalanamadı: " + ex.Message; }
        }

        private void Print()
        {
            var dlg = new PrintDialog();
            if (dlg.ShowDialog() != true) return;
            var xaml = System.Windows.Markup.XamlWriter.Save(_rtb.Document);
            var copy = (FlowDocument)System.Windows.Markup.XamlReader.Parse(xaml);
            copy.PageWidth = dlg.PrintableAreaWidth; copy.PageHeight = dlg.PrintableAreaHeight; copy.PagePadding = new Thickness(72); copy.ColumnWidth = double.PositiveInfinity;
            dlg.PrintDocument(((IDocumentPaginatorSource)copy).DocumentPaginator, BaseName);
        }

        private void OpenSource()
        {
            if (_main is null || _vm?.Source is null) return;
            // Çalışma kopyası (tanınmış, aranabilir) yeni sekmede açılır; kaydedince hedef sorulur.
            var tmp = Path.Combine(AppPaths.Temp, $"ocr-kaynak-{Guid.NewGuid():N}.pdf");
            _ = Task.Run(async () =>
            {
                await _vm.Source.SaveCopyAsync(tmp);
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    var d = await PdfDocument.OpenAsync(tmp);
                    d.SetSaveTarget(null, markModified: true);
                    _main.AddTab(d, record: false).StatusText = "Tanınmış (aranabilir) belge — kaydetmek için Ctrl+S.";
                });
            });
        }

        /// <summary>Öz test için.</summary>
        public RichTextBox Editor => _rtb;
    }
}
