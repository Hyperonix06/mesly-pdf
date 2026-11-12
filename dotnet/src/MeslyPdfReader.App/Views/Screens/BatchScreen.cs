using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.Core;
using MeslyPdfReader.Data;
using MeslyPdfReader.Ocr;
using MeslyPdfReader.Pdf;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MeslyPdfReader.App.ViewModels
{
    public enum BatchStatus { Waiting, Running, Done, Error, Cancelled }

    public sealed partial class BatchItem : ObservableObject
    {
        public required string Path { get; init; }
        public string Name => System.IO.Path.GetFileName(Path);
        [ObservableProperty] private ConvertFormat _format;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsRunning))] private BatchStatus _status;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText))] private double _progress;
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusText))] private string _message = "";
        public string? Output { get; set; }
        public bool IsRunning => Status == BatchStatus.Running;
        public string StatusText => Status switch
        {
            BatchStatus.Waiting => "Beklemede…",
            BatchStatus.Running => $"İşleniyor… %{Progress * 100:0}",
            BatchStatus.Done => "Tamamlandı",
            BatchStatus.Cancelled => "Durduruldu",
            _ => "Hata: " + Message,
        };
    }

    /// <summary>Afiş 7 — Toplu Dönüştürme / Kuyruk Yönetimi.</summary>
    public sealed partial class BatchViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private readonly AppSettings _s;
        private readonly AppDatabase _db;
        private CancellationTokenSource? _cts;

        public BatchViewModel(MainViewModel main, AppSettings s, AppDatabase db) { _main = main; _s = s; _db = db; }

        public ObservableCollection<BatchItem> Items { get; } = new();
        [ObservableProperty] [NotifyPropertyChangedFor(nameof(SummaryText))] private bool _isRunning;
        [ObservableProperty] private double _overall;
        [ObservableProperty] private ConvertFormat _defaultFormat = ConvertFormat.Word;

        public string SummaryText =>
            $"Toplam: {Items.Count} dosya  |  Tamamlanan: {Items.Count(i => i.Status == BatchStatus.Done)}  |  İşleniyor: {Items.Count(i => i.Status == BatchStatus.Running)}" +
            (Items.Any(i => i.Status == BatchStatus.Error) ? $"  |  Hata: {Items.Count(i => i.Status == BatchStatus.Error)}" : "");

        public void NotifySummary() => OnPropertyChanged(nameof(SummaryText));

        public int Add(IEnumerable<string> files)
        {
            int n = 0;
            foreach (var f in files.Where(FileDialogs.IsPdf).Select(Path.GetFullPath))
            {
                if (Items.Any(i => string.Equals(i.Path, f, StringComparison.OrdinalIgnoreCase))) continue;
                Items.Add(new BatchItem { Path = f, Format = DefaultFormat });
                n++;
            }
            NotifySummary();
            return n;
        }

        public void AddFiles()
        {
            var f = FileDialogs.OpenFiles(FileDialogs.PdfFilter, true, "Dönüştürülecek PDF'leri seçin");
            if (f.Length > 0) Add(f);
        }

        public void AddFolder()
        {
            var dir = FileDialogs.PickFolder("PDF'lerin bulunduğu klasör");
            if (dir is null) return;
            List<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.pdf", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }).Take(1000).ToList(); }
            catch (Exception ex) { MessageDialog.Show("Klasör okunamadı", ex.Message, MessageKind.Error); return; }
            int n = Add(files);
            _main.StatusText = n == 0 ? "Klasörde yeni PDF bulunamadı." : $"{n} PDF kuyruğa eklendi.";
        }

        public void Clear()
        {
            if (IsRunning) return;
            Items.Clear();
            Overall = 0;
            NotifySummary();
        }

        public void Remove(BatchItem item) { if (!item.IsRunning) { Items.Remove(item); NotifySummary(); } }

        public void Stop() => _cts?.Cancel();

        public string OutputFor(BatchItem item)
        {
            var dir = !string.IsNullOrWhiteSpace(_s.BatchOutputFolder) && Directory.Exists(_s.BatchOutputFolder) ? _s.BatchOutputFolder! : Path.GetDirectoryName(item.Path)!;
            var ext = ConvertWorkflow.Extension(item.Format, _s.ExportImageFormat == 1 ? ImageFormatKind.Jpeg : ImageFormatKind.Png);
            return PdfTools.UniquePath(Path.Combine(dir, Path.GetFileNameWithoutExtension(item.Path) + ext));
        }

        /// <summary>Bekleyen ve hatalı öğeleri sırayla dönüştürür. Durdur: çalışan öğe iptal edilir, kalanlar bekler.</summary>
        public async Task RunAsync()
        {
            if (IsRunning) return;
            var queue = Items.Where(i => i.Status is BatchStatus.Waiting or BatchStatus.Error or BatchStatus.Cancelled).ToList();
            if (queue.Count == 0) { _main.StatusText = Items.Count == 0 ? "Kuyruk boş. Dosya veya klasör ekleyin." : "Tüm dosyalar zaten dönüştürüldü."; return; }
            foreach (var q in queue) { q.Status = BatchStatus.Waiting; q.Progress = 0; q.Message = ""; }
            IsRunning = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            int done = 0;
            try
            {
                foreach (var item in queue)
                {
                    if (ct.IsCancellationRequested) break;
                    item.Status = BatchStatus.Running;
                    NotifySummary();
                    long? task = null;
                    try { task = _db.StartTask(ConvertWorkflow.TaskKind(item.Format), item.Name); } catch { }
                    try
                    {
                        var target = OutputFor(item);
                        using var doc = await _main.OpenWithPasswordAsync(item.Path) ?? throw new OperationCanceledException("Parola girilmedi.");
                        var req = new ConvertRequest(item.Format, Enumerable.Range(0, doc.PageCount).ToList(), _s.ConvertOcr, OcrRunOptions.From(_s),
                            _s.ConvertTables, _s.ConvertImages, _s.ConvertSheetPerPage, _s.ConvertTablesOnly,
                            _s.ExportImageFormat == 1 ? ImageFormatKind.Jpeg : ImageFormatKind.Png, _s.ExportDpi, _s.JpegQuality);
                        var p = new Progress<double>(v => { item.Progress = v; Overall = (done + v) / queue.Count * 100; });
                        var r = await ConvertWorkflow.ConvertAsync(doc, target, req, p, ct);
                        item.Output = r.Path;
                        item.Progress = 1;
                        item.Status = BatchStatus.Done;
                        if (task is { } id) try { _db.FinishTask(id, "ok", r.Path, null); } catch { }
                    }
                    catch (OperationCanceledException)
                    {
                        item.Status = BatchStatus.Cancelled;
                        if (task is { } id) try { _db.FinishTask(id, "cancelled", null, "Durduruldu"); } catch { }
                        if (ct.IsCancellationRequested) break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Toplu dönüştürme: " + item.Path, ex);
                        item.Message = ex is PdfException or OcrException ? ex.Message : ex.Message;
                        item.Status = BatchStatus.Error;
                        if (task is { } id) try { _db.FinishTask(id, "error", null, ex.Message); } catch { }
                    }
                    done++;
                    Overall = done * 100.0 / queue.Count;
                    NotifySummary();
                }
            }
            finally
            {
                IsRunning = false;
                _cts.Dispose(); _cts = null;
                NotifySummary();
                _main.Library.RefreshStats();
            }
            int ok = queue.Count(i => i.Status == BatchStatus.Done), err = queue.Count(i => i.Status == BatchStatus.Error);
            _main.StatusText = $"Toplu dönüştürme: {ok} tamamlandı" + (err > 0 ? $", {err} hata" : "") + (queue.Any(i => i.Status is BatchStatus.Cancelled or BatchStatus.Waiting) ? ", kalanlar durduruldu." : ".");
        }
    }
}

namespace MeslyPdfReader.App.Views.Screens
{
    public sealed class BatchScreen : UserControl
    {
        private static readonly ConvertFormat[] Fmts = { ConvertFormat.Word, ConvertFormat.Excel, ConvertFormat.PowerPoint, ConvertFormat.Image, ConvertFormat.Text };
        private static readonly string[] FmtNames = { "Word (.docx)", "Excel (.xlsx)", "PPT (.pptx)", "Resim (.png/.jpg)", "TXT (.txt)" };

        private BatchViewModel? _vm;
        private MainViewModel? _main;
        private readonly HashSet<BatchItem> _hooked = new();
        private readonly StackPanel _rows = new();
        private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
        private readonly ProgressBar _bar = new() { Height = 6, Width = 220, Margin = new Thickness(16, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Maximum = 100 };
        private readonly TextBlock _pct = new() { VerticalAlignment = VerticalAlignment.Center, Width = 44 };
        private readonly System.Windows.Controls.Button _start, _stop, _clear;
        private readonly TextBlock _empty;

        public BatchScreen()
        {
            AllowDrop = true;
            var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            var add = Kit.Secondary("Dosya Ekle", SymbolRegular.Add24, () => _vm?.AddFiles());
            var folder = Kit.Secondary("Klasör Ekle", SymbolRegular.FolderAdd24, () => _vm?.AddFolder());
            _clear = Kit.Secondary("Tümünü Temizle", SymbolRegular.Delete24, () => _vm?.Clear());
            foreach (var b in new[] { add, folder, _clear }) { b.Margin = new Thickness(0, 0, 8, 0); tools.Children.Add(b); }
            var def = Kit.Combo(FmtNames, 0, i => { if (_vm is not null) _vm.DefaultFormat = Fmts[i]; });
            def.ToolTip = "Yeni eklenen dosyaların biçimi";
            tools.Children.Add(new TextBlock { Text = "Yeni dosyalar için biçim:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 8, 0) });
            tools.Children.Add(def);

            var header = Row(new TextBlock { Text = "Dosya Adı", FontWeight = FontWeights.SemiBold }, new TextBlock { Text = "Biçim", FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = "Durum", FontWeight = FontWeights.SemiBold }, new Border());
            header.Margin = new Thickness(0, 0, 0, 4);
            _empty = Kit.Muted("Kuyruk boş. “Dosya Ekle” veya “Klasör Ekle” ile PDF ekleyin ya da dosyaları buraya sürükleyin.", 12.5);
            _empty.Margin = new Thickness(4, 14, 0, 14);
            var list = new StackPanel();
            list.Children.Add(header);
            list.Children.Add(Kit.Divider(2, 2));
            list.Children.Add(_empty);
            list.Children.Add(_rows);
            var listCard = Kit.Card(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            _stop = Kit.Primary("Durdur", SymbolRegular.Stop24, () => _vm?.Stop());
            _start = Kit.Primary("Başlat", SymbolRegular.Play24, () => _ = _vm?.RunAsync());
            _start.MinWidth = _stop.MinWidth = 120;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(_start); buttons.Children.Add(_stop);
            DockPanel.SetDock(buttons, Dock.Right);
            footer.Children.Add(buttons);
            var out_ = Kit.Secondary("Çıktı klasörü…", SymbolRegular.Folder24, PickOutput);
            out_.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(out_, Dock.Right);
            footer.Children.Add(out_);
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(_summary); left.Children.Add(_bar); left.Children.Add(_pct);
            footer.Children.Add(left);

            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition());
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.Children.Add(tools);
            Grid.SetRow(listCard, 1); body.Children.Add(listCard);
            var ocr = new System.Windows.Controls.CheckBox { Content = "Taranmış sayfaları önce tanı (OCR)", Margin = new Thickness(0, 10, 0, 0) };
            ocr.Checked += (_, _) => { if (_main is not null) _main.Settings.ConvertOcr = true; };
            ocr.Unchecked += (_, _) => { if (_main is not null) _main.Settings.ConvertOcr = false; };
            IsVisibleChanged += (_, _) => { if (_main is not null) ocr.IsChecked = _main.Settings.ConvertOcr; };
            Grid.SetRow(ocr, 2); body.Children.Add(ocr);
            Grid.SetRow(footer, 3); body.Children.Add(footer);
            Content = Kit.Root("Toplu Dönüştürme", body);

            DataContextChanged += (_, _) => Bind();
            Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] f && _vm is not null) { e.Handled = true; _vm.Add(f.SelectMany(Expand)); } };
        }

        private static IEnumerable<string> Expand(string p) => Directory.Exists(p)
            ? Directory.EnumerateFiles(p, "*.pdf", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }).Take(1000)
            : new[] { p };

        private void PickOutput()
        {
            if (_main is null) return;
            var s = _main.Settings;
            var f = FileDialogs.PickFolder("Dönüştürülen dosyaların kaydedileceği klasör (iptal: her dosya kendi klasörüne)", s.BatchOutputFolder);
            s.BatchOutputFolder = f;
            _main.StatusText = f is null ? "Çıktılar her dosyanın kendi klasörüne yazılacak." : $"Çıktı klasörü: {f}";
        }

        private void Bind()
        {
            _main = DataContext as MainViewModel;
            if (_vm is not null) { _vm.PropertyChanged -= OnVm; _vm.Items.CollectionChanged -= OnItems; }
            _vm = _main?.Batch;
            if (_vm is null) return;
            _vm.PropertyChanged += OnVm;
            _vm.Items.CollectionChanged += OnItems;
            Rebuild();
        }

        private void OnItems(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Rebuild();
        private void OnVm(object? s, System.ComponentModel.PropertyChangedEventArgs e) => UpdateFooter();

        private static Grid Row(UIElement name, UIElement fmt, UIElement status, UIElement action)
        {
            var g = new Grid { MinHeight = 34 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            foreach (var (e, c) in new[] { (name, 0), (fmt, 1), (status, 2), (action, 3) })
            {
                if (e is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(e, c); g.Children.Add(e);
            }
            return g;
        }

        private void Rebuild()
        {
            _rows.Children.Clear();
            if (_vm is null) return;
            foreach (var item in _vm.Items)
            {
                var name = new StackPanel { Orientation = Orientation.Horizontal };
                name.Children.Add(Kit.PdfIcon(16).Also(i => i.Margin = new Thickness(0, 0, 8, 0)));
                name.Children.Add(new TextBlock { Text = item.Name, ToolTip = item.Path, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 });
                var fmt = Kit.Combo(FmtNames, Array.IndexOf(Fmts, item.Format), i => item.Format = Fmts[i]);
                fmt.MinWidth = 150; fmt.Margin = new Thickness(0, 2, 12, 2);
                fmt.SetBinding(IsEnabledProperty, new Binding(nameof(BatchItem.Status)) { Source = item, Converter = new FuncConverter(v => (BatchStatus)v! is not BatchStatus.Running and not BatchStatus.Done) });
                var status = new StackPanel { Orientation = Orientation.Horizontal };
                var icon = new SymbolIcon { FontSize = 15, Margin = new Thickness(0, 0, 6, 0) };
                icon.SetBinding(SymbolIcon.SymbolProperty, new Binding(nameof(BatchItem.Status)) { Source = item, Converter = new FuncConverter(v => (BatchStatus)v! switch
                {
                    BatchStatus.Done => SymbolRegular.CheckmarkCircle24, BatchStatus.Running => SymbolRegular.ArrowSync24,
                    BatchStatus.Error => SymbolRegular.ErrorCircle24, BatchStatus.Cancelled => SymbolRegular.Pause24, _ => SymbolRegular.Clock24,
                }) });
                icon.SetBinding(ForegroundProperty, new Binding(nameof(BatchItem.Status)) { Source = item, Converter = new FuncConverter(v => (BatchStatus)v! switch
                {
                    BatchStatus.Done => Kit.Brush("MeslySuccessBrush"), BatchStatus.Error => Kit.Brush("MeslyAccentBrush"), BatchStatus.Running => Brushes.RoyalBlue, _ => Kit.Brush("MeslyMutedTextBrush"),
                }) });
                var st = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                st.SetBinding(TextBlock.TextProperty, new Binding(nameof(BatchItem.StatusText)) { Source = item });
                st.SetBinding(ToolTipProperty, new Binding(nameof(BatchItem.StatusText)) { Source = item });
                status.Children.Add(icon); status.Children.Add(st);
                var act = item.Status == BatchStatus.Done && item.Output is { } o
                    ? Kit.IconButton(SymbolRegular.FolderOpen24, "Klasörde göster", () => { try { Process.Start("explorer.exe", $"/select,\"{o}\""); } catch { } })
                    : Kit.IconButton(SymbolRegular.Dismiss24, "Kuyruktan kaldır", () => _vm.Remove(item));
                if (_hooked.Add(item))
                    item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BatchItem.Status) && item.Status is BatchStatus.Done && _vm.Items.Contains(item)) Dispatcher.BeginInvoke(Rebuild); };
                _rows.Children.Add(Row(name, fmt, status, act));
            }
            UpdateFooter();
        }

        private void UpdateFooter()
        {
            if (_vm is null) return;
            _empty.Visibility = _vm.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _summary.Text = _vm.SummaryText;
            _bar.Value = _vm.Overall;
            _pct.Text = $"%{_vm.Overall:0}";
            _start.Visibility = _vm.IsRunning ? Visibility.Collapsed : Visibility.Visible;
            _stop.Visibility = _vm.IsRunning ? Visibility.Visible : Visibility.Collapsed;
            _start.IsEnabled = _vm.Items.Count > 0;
            _clear.IsEnabled = !_vm.IsRunning && _vm.Items.Count > 0;
        }
    }

    /// <summary>Kodla bağlama için basit tek yönlü dönüştürücü.</summary>
    internal sealed class FuncConverter(Func<object?, object?> f) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => f(value);
        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}
