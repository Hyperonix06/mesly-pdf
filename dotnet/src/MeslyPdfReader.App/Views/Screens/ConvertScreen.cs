using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.Core;
using MeslyPdfReader.Data;
using MeslyPdfReader.Ocr;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using PageRange = MeslyPdfReader.Core.PageRange;

namespace MeslyPdfReader.App.ViewModels
{
    /// <summary>Afiş 6 — Dönüştür (PDF → Word, Excel, PPT, Resim, TXT).</summary>
    public sealed partial class ConvertScreenViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private readonly AppSettings _s;

        public ConvertScreenViewModel(MainViewModel main, AppSettings settings) { _main = main; _s = settings; }

        [ObservableProperty] private SourcePdf? _source;
        [ObservableProperty] private ConvertFormat _format = ConvertFormat.Word;
        [ObservableProperty] private string _rangeText = "";
        public bool HasSource => Source is not null;
        public ConvertResult? LastResult { get; private set; }
        public string? LastError { get; private set; }

        partial void OnSourceChanged(SourcePdf? oldValue, SourcePdf? newValue)
        {
            oldValue?.Dispose();
            RangeText = newValue is null ? "" : AllPagesText(newValue.PageCount);
            OnPropertyChanged(nameof(HasSource));
        }

        public static string AllPagesText(int n) => $"Tümü (1 – {n})";

        /// <summary>Kaynağı ayarlar (Belgelerim'den “Dönüştür”, sürükle-bırak, PDF seç).</summary>
        public async void SetSource(string path) => await SetSourceAsync(path);

        public async Task<bool> SetSourceAsync(string path)
        {
            try
            {
                var src = await SourcePdf.OpenAsync(_main, path);
                if (src is null) return false;
                Source = src;
                return true;
            }
            catch (Exception ex) { BusyViewModel.ShowError("PDF açma", ex); return false; }
        }

        public void PickSource()
        {
            var f = FileDialogs.OpenFiles(FileDialogs.PdfFilter, false, "Dönüştürülecek PDF'yi seçin");
            if (f.Length > 0) SetSource(f[0]);
        }

        public void ClearSource() => Source = null;

        /// <summary>Kaynak açık bir sekmeyse ve sekme kapatıldıysa kaynağı temizler.</summary>
        public void Validate()
        {
            if (Source?.Tab is { } t && !_main.Tabs.Contains(t)) Source = null;
        }

        public bool TryPages(out IReadOnlyList<int> pages, out string? error)
        {
            pages = Array.Empty<int>(); error = null;
            if (Source is null) { error = "Önce bir PDF seçin."; return false; }
            var t = RangeText.Trim();
            if (t.Length == 0 || t.StartsWith("Tümü", StringComparison.OrdinalIgnoreCase)) { pages = Enumerable.Range(0, Source.PageCount).ToList(); return true; }
            return PageRange.TryParse(t, Source.PageCount, out pages, out error);
        }

        public ConvertRequest BuildRequest(IReadOnlyList<int> pages) => new(
            Format, pages, _s.ConvertOcr, OcrRunOptions.From(_s), _s.ConvertTables, _s.ConvertImages, _s.ConvertSheetPerPage, _s.ConvertTablesOnly,
            _s.ExportImageFormat == 1 ? ImageFormatKind.Jpeg : ImageFormatKind.Png, _s.ExportDpi, _s.JpegQuality);

        public bool Ocr { get => _s.ConvertOcr; set { _s.ConvertOcr = value; OnPropertyChanged(); } }

        /// <summary>Dönüştürür. <paramref name="target"/> verilmezse kayıt yeri sorulur.</summary>
        public async Task<ConvertResult?> ConvertAsync(string? target = null)
        {
            Validate();
            LastResult = null; LastError = null;
            if (!TryPages(out var pages, out var err)) { LastError = err; MessageDialog.Show("Dönüştürülemedi", err ?? "Sayfa aralığı geçersiz.", MessageKind.Warning); return null; }
            var src = Source!;
            var ext = ConvertWorkflow.Extension(Format, _s.ExportImageFormat == 1 ? ImageFormatKind.Jpeg : ImageFormatKind.Png);
            if (target is null)
            {
                var filter = Format switch
                {
                    ConvertFormat.Word => "Word belgesi (*.docx)|*.docx",
                    ConvertFormat.Excel => "Excel çalışma kitabı (*.xlsx)|*.xlsx",
                    ConvertFormat.PowerPoint => "PowerPoint sunusu (*.pptx)|*.pptx",
                    ConvertFormat.Text => "Metin dosyası (*.txt)|*.txt",
                    _ => ext == ".jpg" ? "JPEG resmi (*.jpg)|*.jpg" : "PNG resmi (*.png)|*.png",
                };
                var dir = File.Exists(src.Path) ? Path.GetDirectoryName(src.Path) : null;
                target = FileDialogs.SaveFile(Path.GetFileNameWithoutExtension(src.Name) + ext, filter, dir,
                    Format == ConvertFormat.Image && pages.Count > 1 ? "Resimlerin adı (her sayfa _s001, _s002… olarak kaydedilir)" : "Dönüştürülen dosyayı kaydet");
                if (target is null) return null;
            }
            var req = BuildRequest(pages);
            ConvertResult? result = null;
            var ok = await _main.RunTaskAsync($"{src.Name} dönüştürülüyor ({ConvertWorkflow.DisplayName(Format)}, {pages.Count} sayfa)…",
                async (p, ct) => result = await ConvertWorkflow.ConvertAsync(src.Document, target, req, p, ct),
                ConvertWorkflow.TaskKind(Format), src.Name, () => target);
            if (!ok || result is null) { LastError = "İşlem tamamlanmadı."; return null; }
            LastResult = result;
            var summary = ConvertWorkflow.Summarize(result);
            _main.StatusText = $"{Path.GetFileName(result.Path)}: {summary}";
            _main.Library.RefreshStats();
            if (DialogBase.Automation is null) Offer(result, summary);
            return result;
        }

        private void Offer(ConvertResult r, string summary)
        {
            if (r.Files.Count > 1)
            {
                if (MessageDialog.Confirm("Dönüştürme tamamlandı", summary + "\n\nKlasör açılsın mı?", "Klasörü aç", "Kapat", MessageKind.Success))
                    try { Process.Start("explorer.exe", $"/select,\"{r.Files[0]}\""); } catch { }
                return;
            }
            if (MessageDialog.Confirm("Dönüştürme tamamlandı", summary + $"\n\n{r.Path}\n\nDosya açılsın mı?", "Aç", "Kapat", MessageKind.Success))
            {
                try { Process.Start(new ProcessStartInfo(r.Path) { UseShellExecute = true }); }
                catch (Exception ex) { MessageDialog.Show("Dosya açılamadı", $"Dosya kaydedildi ancak açılamadı (bu dosya türü için kayıtlı bir uygulama olmayabilir):\n{ex.Message}", MessageKind.Warning); }
            }
        }
    }
}

namespace MeslyPdfReader.App.Views.Screens
{
    public sealed class ConvertScreen : UserControl
    {
        private static readonly (ConvertFormat Format, string Name, string Ext, SymbolRegular Icon, string Color)[] Formats =
        {
            (ConvertFormat.Word, "Word", "(.docx)", SymbolRegular.DocumentText24, "#2B579A"),
            (ConvertFormat.Excel, "Excel", "(.xlsx)", SymbolRegular.Table24, "#217346"),
            (ConvertFormat.PowerPoint, "PPT", "(.pptx)", SymbolRegular.SlideText24, "#D24726"),
            (ConvertFormat.Image, "Resim", "(.png/.jpg)", SymbolRegular.Image24, "#7B1FA2"),
            (ConvertFormat.Text, "TXT", "(.txt)", SymbolRegular.TextT24, "#4B5563"),
        };

        private ConvertScreenViewModel? _vm;
        private MainViewModel? _main;
        private readonly ContentControl _sourceHost = new();
        private readonly Dictionary<ConvertFormat, RadioButton> _tiles = new();
        private readonly ComboBox _range = new() { IsEditable = true, MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Tümü ya da 1-3, 5, 8-10 gibi bir aralık yazın" };
        private readonly StackPanel _imageOptions = new() { Visibility = Visibility.Collapsed };
        private readonly StackPanel _docOptions = new();
        private readonly System.Windows.Controls.CheckBox _ocr = new() { Content = "OCR ile metin tanıma (gerekirse)", Margin = new Thickness(0, 10, 0, 0), ToolTip = "Metni olmayan taranmış sayfalar dönüştürmeden önce bu bilgisayarda tanınır; özgün belge değişmez." };
        private readonly System.Windows.Controls.Button _convert;

        public ConvertScreen()
        {
            AllowDrop = true;
            var body = new StackPanel();

            body.Children.Add(Kit.Card(_sourceHost, new Thickness(16, 12, 12, 12)));

            body.Children.Add(Kit.Section("Dönüştürme Biçimi", 20));
            var tiles = new WrapPanel();
            foreach (var f in Formats)
            {
                var sp = new StackPanel();
                var icon = new SymbolIcon { Symbol = f.Icon, FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, Filled = true, Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(f.Color)) };
                sp.Children.Add(icon);
                sp.Children.Add(new TextBlock { Text = f.Name, FontWeight = FontWeights.SemiBold, FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
                sp.Children.Add(Kit.Muted(f.Ext, 10.5).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center));
                var rb = new RadioButton { Content = sp, GroupName = "convFmt", ToolTip = $"PDF → {f.Name} {f.Ext}", Width = 92, Height = 86, Margin = new Thickness(0, 0, 10, 10) };
                rb.SetResourceReference(StyleProperty, "FormatTile");
                var fmt = f.Format;
                rb.Checked += (_, _) => { if (_vm is not null) { _vm.Format = fmt; UpdateOptions(); } };
                _tiles[fmt] = rb;
                tiles.Children.Add(rb);
            }
            body.Children.Add(tiles);

            body.Children.Add(Kit.Section("Ayarlar", 12));
            _range.LostFocus += (_, _) => { if (_vm is not null) _vm.RangeText = _range.Text; };
            _range.SelectionChanged += (_, _) => { if (_vm is not null && _range.SelectedItem is string s) _vm.RangeText = s; };
            body.Children.Add(Kit.Field("Sayfa aralığı:", _range, 120));
            body.Children.Add(_docOptions);
            body.Children.Add(_imageOptions);
            _ocr.Checked += (_, _) => { if (_vm is not null) _vm.Ocr = true; };
            _ocr.Unchecked += (_, _) => { if (_vm is not null) _vm.Ocr = false; };
            body.Children.Add(_ocr);

            var footer = new DockPanel { Margin = new Thickness(0, 22, 0, 0), LastChildFill = false };
            _convert = Kit.Primary("Dönüştür", SymbolRegular.ArrowSync24, () => _ = ConvertNow());
            _convert.MinWidth = 150;
            DockPanel.SetDock(_convert, Dock.Right);
            footer.Children.Add(_convert);
            var batch = Kit.Secondary("Toplu Dönüştürme", SymbolRegular.DocumentMultiple24, () => _main?.Navigate(AppScreen.BatchConvert));
            batch.ToolTip = "Çok sayıda PDF'yi kuyrukla dönüştürün";
            DockPanel.SetDock(batch, Dock.Left);
            footer.Children.Add(batch);
            var officePdf = Kit.Secondary("Office → PDF", SymbolRegular.DocumentPdf24, () => _main?.OfficeToPdfCommand.Execute(null));
            officePdf.Margin = new Thickness(8, 0, 0, 0);
            officePdf.ToolTip = "Word, Excel veya PowerPoint belgelerini PDF'ye çevirin (Microsoft Office veya LibreOffice gerekir)";
            DockPanel.SetDock(officePdf, Dock.Left);
            footer.Children.Add(officePdf);
            body.Children.Add(footer);

            Content = Kit.Root("PDF Dönüştürme", new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            DataContextChanged += (_, _) => Bind();
            IsVisibleChanged += (_, e) => { if (e.NewValue is true) { _vm?.Validate(); UseActiveTabIfEmpty(); Refresh(); } };
            Drop += async (_, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.FirstOrDefault(FileDialogs.IsPdf) is { } pdf && _vm is not null)
                { e.Handled = true; await _vm.SetSourceAsync(pdf); }
            };
        }

        private void Bind()
        {
            _main = DataContext as MainViewModel;
            if (_vm is not null) _vm.PropertyChanged -= OnVm;
            _vm = _main?.Converter;
            if (_vm is null) return;
            _vm.PropertyChanged += OnVm;
            Refresh();
        }

        private void OnVm(object? s, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName is nameof(ConvertScreenViewModel.Source)) Refresh(); }

        /// <summary>Kaynak seçilmemişse ve görüntüleyicide açık bir belge varsa onu öner.</summary>
        private void UseActiveTabIfEmpty()
        {
            if (_vm is { Source: null } && _main?.ActiveTab is { } t) _vm.Source = SourcePdf.FromTab(t);
        }

        private void Refresh()
        {
            if (_vm is null) return;
            _tiles[_vm.Format].IsChecked = true;
            _ocr.IsChecked = _vm.Ocr;
            _range.Items.Clear();
            if (_vm.Source is { } src)
            {
                _range.Items.Add(ConvertScreenViewModel.AllPagesText(src.PageCount));
                if (src.Tab is { } tab && src.PageCount > 1) _range.Items.Add((tab.CurrentPage + 1).ToString());
                _range.Text = _vm.RangeText;
                _range.IsEnabled = true;
            }
            else { _range.Text = ""; _range.IsEnabled = false; }
            _convert.IsEnabled = _vm.HasSource;
            _sourceHost.Content = SourceView();
            UpdateOptions();
        }

        private FrameworkElement SourceView()
        {
            var g = new DockPanel();
            if (_vm?.Source is not { } src)
            {
                var pick = Kit.Primary("PDF Seç", SymbolRegular.FolderOpen24, () => _vm?.PickSource());
                DockPanel.SetDock(pick, Dock.Right);
                g.Children.Add(pick);
                var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                t.Children.Add(new TextBlock { Text = "Dönüştürülecek PDF'yi seçin", FontWeight = FontWeights.SemiBold });
                t.Children.Add(Kit.Muted("veya dosyayı buraya sürükleyip bırakın"));
                g.Children.Add(t);
                return g;
            }
            var close = Kit.IconButton(SymbolRegular.Dismiss24, "Kaynağı kaldır", () => _vm.ClearSource());
            DockPanel.SetDock(close, Dock.Right);
            g.Children.Add(close);
            var change = Kit.Secondary("Değiştir", null, () => _vm.PickSource());
            change.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(change, Dock.Right);
            g.Children.Add(change);
            var icon = Kit.PdfIcon(30);
            icon.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(icon, Dock.Left);
            g.Children.Add(icon);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = src.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = src.Path });
            info.Children.Add(Kit.Muted(src.Info));
            g.Children.Add(info);
            return g;
        }

        private void UpdateOptions()
        {
            if (_vm is null || _main is null) return;
            var s = _main.Settings;
            _docOptions.Children.Clear();
            _imageOptions.Children.Clear();
            _imageOptions.Visibility = _vm.Format == ConvertFormat.Image ? Visibility.Visible : Visibility.Collapsed;
            _ocr.Visibility = _vm.Format == ConvertFormat.Image ? Visibility.Collapsed : Visibility.Visible;
            switch (_vm.Format)
            {
                case ConvertFormat.Word or ConvertFormat.PowerPoint:
                    _docOptions.Children.Add(Kit.Check("Tabloları algıla", s.ConvertTables, v => s.ConvertTables = v, "Çizgili tablolar gerçek tabloya dönüşür."));
                    _docOptions.Children.Add(Kit.Check("Resimleri ekle", s.ConvertImages, v => s.ConvertImages = v));
                    break;
                case ConvertFormat.Excel:
                    _docOptions.Children.Add(Kit.Check("Tabloları algıla", s.ConvertTables, v => s.ConvertTables = v));
                    _docOptions.Children.Add(Kit.Check("Her sayfa ayrı çalışma sayfası", s.ConvertSheetPerPage, v => s.ConvertSheetPerPage = v));
                    _docOptions.Children.Add(Kit.Check("Yalnızca tabloları aktar", s.ConvertTablesOnly, v => s.ConvertTablesOnly = v));
                    break;
                case ConvertFormat.Image:
                    var dpis = new[] { 96, 150, 200, 300, 600 };
                    _imageOptions.Children.Add(Kit.Field("Resim türü:", Kit.Combo(new[] { "PNG (kayıpsız)", "JPG (küçük dosya)" }, s.ExportImageFormat, i => s.ExportImageFormat = i).Also(c => c.HorizontalAlignment = HorizontalAlignment.Left), 120));
                    _imageOptions.Children.Add(Kit.Field("Çözünürlük:", Kit.Combo(dpis.Select(d => d + " DPI"), Math.Max(0, Array.IndexOf(dpis, s.ExportDpi)), i => s.ExportDpi = dpis[i]).Also(c => c.HorizontalAlignment = HorizontalAlignment.Left), 120));
                    break;
            }
        }

        private async Task ConvertNow()
        {
            if (_vm is null) return;
            _vm.RangeText = _range.Text;
            await _vm.ConvertAsync();
            Refresh();
        }

        /// <summary>Öz test için: biçim kutusunu seçer.</summary>
        public void SelectFormat(ConvertFormat f) => _tiles[f].IsChecked = true;
        public int TileCount => _tiles.Count;
    }

    internal static class FluentExt
    {
        public static T Also<T>(this T o, Action<T> a) { a(o); return o; }
    }
}
