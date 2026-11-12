using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeslyPdfReader.App.Dialogs;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.Core;
using MeslyPdfReader.Pdf;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using PageRange = MeslyPdfReader.Core.PageRange;

namespace MeslyPdfReader.App.Views.Screens;

/// <summary>Afiş 9 — Sayfa Yönetimi: Birleştir, Böl, Sırala (sürükle-bırak; Sıfırla / Uygula).</summary>
public sealed class PageManagerScreen : UserControl
{
    private MainViewModel? _main;
    private readonly Dictionary<string, RadioButton> _tabs = new();
    private readonly ContentControl _body = new();
    public string CurrentTab { get; private set; } = "order";

    // Sırala
    private DocumentViewModel? _orderDoc;
    private readonly List<int> _order = new();
    private int _selected = -1;
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _stripScroll;
    private readonly Dictionary<int, ImageSource?> _thumbCache = new();
    private int _thumbRevision = -1;
    private Point _dragStart;

    // Birleştir
    private readonly ObservableCollection<(string Path, string Range)> _merge = new();

    public PageManagerScreen()
    {
        var tabRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        foreach (var (key, text) in new[] { ("merge", "Birleştir"), ("split", "Böl"), ("order", "Sırala") })
        {
            var rb = (RadioButton)Kit.Segment(text, "pmTabs");
            var k = key;
            rb.Checked += (_, _) => Show(k);
            _tabs[key] = rb;
            tabRow.Children.Add(rb);
        }
        _stripScroll = new ScrollViewer { Content = _strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(tabRow);
        Grid.SetRow(_body, 1);
        root.Children.Add(_body);
        Content = Kit.Root("Sayfa Yönetimi", root);
        DataContextChanged += (_, _) => _main = DataContext as MainViewModel;
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Show(CurrentTab); };
        _tabs["order"].IsChecked = true;
    }

    public void Select(string key) { if (_tabs.TryGetValue(key, out var t)) { t.IsChecked = true; Show(key); } }

    private void Show(string key)
    {
        CurrentTab = key;
        _body.Content = key switch { "merge" => MergeView(), "split" => SplitView(), _ => OrderView() };
    }

    // ─────────────── Sırala ───────────────

    private FrameworkElement OrderView()
    {
        if (_orderDoc is not null && (_main is null || !_main.Tabs.Contains(_orderDoc))) { _orderDoc = null; _order.Clear(); }
        // Sıra değiştirilmemişse etkin belgeyi izle (kullanıcının üzerinde çalıştığı belge).
        bool untouched = _order.SequenceEqual(Enumerable.Range(0, _order.Count));
        if (_main?.ActiveTab is { } t && (_orderDoc is null || (t != _orderDoc && untouched))) LoadOrder(t);
        var st = new Grid();
        st.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        st.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        st.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        st.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var pick = Kit.Secondary(_orderDoc is null ? "PDF Seç" : "Başka PDF", SymbolRegular.FolderOpen24, () => _ = PickOrderSource());
        DockPanel.SetDock(pick, Dock.Right);
        head.Children.Add(pick);
        head.Children.Add(new TextBlock { Text = _orderDoc is null ? "Sıralanacak belge seçilmedi." : $"{_orderDoc.Title} — {_orderDoc.PageCount} sayfa", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        st.Children.Add(head);

        var strip = new Grid { Height = 230 };
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strip.ColumnDefinitions.Add(new ColumnDefinition());
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var prev = Kit.IconButton(SymbolRegular.ChevronLeft24, "Sola kaydır", () => _stripScroll.ScrollToHorizontalOffset(_stripScroll.HorizontalOffset - 400), 18);
        var next = Kit.IconButton(SymbolRegular.ChevronRight24, "Sağa kaydır", () => _stripScroll.ScrollToHorizontalOffset(_stripScroll.HorizontalOffset + 400), 18);
        prev.VerticalAlignment = next.VerticalAlignment = VerticalAlignment.Center;
        strip.Children.Add(prev);
        Grid.SetColumn(_stripScroll, 1); strip.Children.Add(_stripScroll);
        Grid.SetColumn(next, 2); strip.Children.Add(next);
        if (_stripScroll.Parent is Grid old && old != strip) old.Children.Remove(_stripScroll);
        var card = Kit.Card(strip);
        card.SetResourceReference(Border.BackgroundProperty, "MeslySubtleBrush");
        Grid.SetRow(card, 1);
        st.Children.Add(card);

        var hint = Kit.Muted("Sayfaları sürükleyip bırakarak sıralayabilirsiniz. Seçili sayfayı Ctrl+← / Ctrl+→ ile de taşıyabilirsiniz.");
        hint.HorizontalAlignment = HorizontalAlignment.Center; hint.Margin = new Thickness(0, 10, 0, 0);
        Grid.SetRow(hint, 2); st.Children.Add(hint);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var reset = Kit.Secondary("Sıfırla", null, ResetOrder);
        var apply = Kit.Primary("Uygula", SymbolRegular.Checkmark24, () => _ = ApplyOrder());
        reset.MinWidth = apply.MinWidth = 110;
        apply.Margin = new Thickness(8, 0, 0, 0);
        bool changed = _order.Count > 0 && !_order.SequenceEqual(Enumerable.Range(0, _order.Count));
        reset.IsEnabled = apply.IsEnabled = changed;
        buttons.Children.Add(reset); buttons.Children.Add(apply);
        Grid.SetRow(buttons, 3); st.Children.Add(buttons);

        _stripScroll.Focusable = true;
        _stripScroll.PreviewKeyDown -= StripKey;
        _stripScroll.PreviewKeyDown += StripKey;
        RebuildStrip();
        return st;
    }

    private void LoadOrder(DocumentViewModel t)
    {
        _orderDoc = t;
        _order.Clear();
        _order.AddRange(Enumerable.Range(0, t.PageCount));
        _selected = -1;
        if (_thumbRevision != t.Document.Revision) { _thumbCache.Clear(); _thumbRevision = t.Document.Revision; }
    }

    private async Task PickOrderSource()
    {
        if (_main is null) return;
        var f = FileDialogs.OpenFiles(FileDialogs.PdfFilter, false, "Sayfaları sıralanacak PDF'yi seçin");
        if (f.Length == 0) return;
        await _main.OpenFilesAsync(f, activate: false);
        var full = Path.GetFullPath(f[0]);
        var tab = _main.Tabs.FirstOrDefault(t => string.Equals(t.Document.FilePath, full, StringComparison.OrdinalIgnoreCase));
        if (tab is not null) { _thumbCache.Clear(); LoadOrder(tab); }
        Show("order");
    }

    private void RebuildStrip()
    {
        _strip.Children.Clear();
        if (_orderDoc is null)
        {
            _strip.Children.Add(new TextBlock { Text = "Bir PDF açın veya “PDF Seç” ile seçin.", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20) });
            return;
        }
        var pending = new List<(int, Image)>();
        for (int pos = 0; pos < _order.Count; pos++)
        {
            int page = _order[pos];
            var img = new Image { Width = 110, Height = 150, Stretch = Stretch.Uniform };
            var paper = new Border { Background = Brushes.White, BorderThickness = new Thickness(pos == _selected ? 2.5 : 1), Child = img, Padding = new Thickness(2) };
            paper.SetResourceReference(Border.BorderBrushProperty, pos == _selected ? "MeslyAccentBrush" : "MeslyBorderBrush");
            var label = new TextBlock { Text = (page + 1).ToString(), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), FontWeight = pos == _selected ? FontWeights.SemiBold : FontWeights.Normal };
            if (pos == _selected) label.SetResourceReference(TextBlock.ForegroundProperty, "MeslyAccentBrush");
            var cell = new StackPanel { Margin = new Thickness(10, 12, 10, 8), Tag = pos, Background = Brushes.Transparent, AllowDrop = true, Cursor = Cursors.Hand, ToolTip = $"Özgün sayfa {page + 1}" };
            cell.Children.Add(paper); cell.Children.Add(label);
            int p0 = pos;
            cell.MouseLeftButtonDown += (_, e) => { _selected = p0; _dragStart = e.GetPosition(this); RebuildStrip(); _stripScroll.Focus(); };
            cell.MouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - _dragStart).Length < 6) return;
                DragDrop.DoDragDrop(cell, new DataObject("mesly-page-pos", p0), DragDropEffects.Move);
            };
            cell.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("mesly-page-pos") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            cell.Drop += (_, e) =>
            {
                if (e.Data.GetData("mesly-page-pos") is not int from || from == p0) return;
                var pt = e.GetPosition(cell);
                int to = pt.X > cell.ActualWidth / 2 ? p0 + 1 : p0;
                MoveTo(from, to);
                e.Handled = true;
            };
            _strip.Children.Add(cell);
            if (_thumbCache.TryGetValue(page, out var cached)) img.Source = cached;
            else pending.Add((page, img));
        }
        _ = LoadThumbs(pending, ++_thumbSeq);
    }

    private int _thumbSeq;

    /// <summary>Küçük resimleri sırayla çizer; şerit yeniden kurulursa eski yükleme durur.</summary>
    private async Task LoadThumbs(List<(int Page, Image Img)> list, int seq)
    {
        var doc = _orderDoc;
        if (doc is null) return;
        foreach (var (page, img) in list)
        {
            if (seq != _thumbSeq || !IsVisible) return;
            try
            {
                var src = ImageCodec.ToBitmapSource(await PdfTools.RenderAtDpiAsync(doc.Document, page, 28));
                _thumbCache[page] = src;
                img.Source = src;
            }
            catch { _thumbCache[page] = null; }
        }
    }

    private void MoveTo(int from, int to)
    {
        if (from < 0 || from >= _order.Count) return;
        int page = _order[from];
        _order.RemoveAt(from);
        if (to > from) to--;
        to = Math.Clamp(to, 0, _order.Count);
        _order.Insert(to, page);
        _selected = to;
        Show("order");
    }

    private void StripKey(object sender, KeyEventArgs e)
    {
        if (_selected < 0 || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.Left && _selected > 0) { MoveTo(_selected, _selected - 1); e.Handled = true; }
        else if (e.Key == Key.Right && _selected < _order.Count - 1) { MoveTo(_selected, _selected + 2); e.Handled = true; }
    }

    private void ResetOrder()
    {
        if (_orderDoc is null) return;
        _order.Clear(); _order.AddRange(Enumerable.Range(0, _orderDoc.PageCount));
        _selected = -1;
        Show("order");
    }

    /// <summary>Yeni sırayı belgeye uygular (geri alınabilir). Öz test de çağırır.</summary>
    public async Task<bool> ApplyOrder(IReadOnlyList<int>? order = null)
    {
        if (_orderDoc is null) return false;
        if (order is not null) { _order.Clear(); _order.AddRange(order); }
        bool ok = await _orderDoc.ReorderPagesAsync(_order.ToList());
        if (ok)
        {
            _thumbCache.Clear();
            LoadOrder(_orderDoc);
            if (_main is not null) _main.StatusText = $"{_orderDoc.Title}: sayfalar yeniden sıralandı. Kaydetmek için belgeyi açıp Ctrl+S'ye basın.";
            Show("order");
        }
        return ok;
    }

    // ─────────────── Birleştir ───────────────

    private FrameworkElement MergeView()
    {
        var st = new StackPanel { MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        tools.Children.Add(Kit.Secondary("Dosya Ekle", SymbolRegular.Add24, () =>
        {
            foreach (var f in FileDialogs.OpenFiles(FileDialogs.PdfFilter, true, "Birleştirilecek PDF'leri seçin")) _merge.Add((f, ""));
            Show("merge");
        }).Also(b => b.Margin = new Thickness(0, 0, 8, 0)));
        tools.Children.Add(Kit.Secondary("Açık belgeleri ekle", SymbolRegular.DocumentMultiple24, () =>
        {
            var unsaved = _main?.Tabs.Where(t => t.Document.FilePath is null || t.Document.IsModified).Select(t => t.Title).ToList() ?? new();
            foreach (var t in _main?.Tabs.Where(t => t.Document.FilePath is { } fp && File.Exists(fp)) ?? Enumerable.Empty<DocumentViewModel>()) _merge.Add((t.Document.FilePath!, ""));
            if (unsaved.Count > 0) MessageDialog.Show("Kaydedilmemiş belgeler", "Birleştirme diskteki dosyaları kullanır. Şu belgelerin son hali henüz kaydedilmedi; önce kaydedin:\n" + string.Join("\n", unsaved), MessageKind.Info);
            Show("merge");
        }).Also(b => b.Margin = new Thickness(0, 0, 8, 0)));
        tools.Children.Add(Kit.Secondary("Tümünü Temizle", SymbolRegular.Delete24, () => { _merge.Clear(); Show("merge"); }));
        st.Children.Add(tools);

        var list = new StackPanel();
        if (_merge.Count == 0) list.Children.Add(Kit.Muted("Birleştirilecek PDF'leri ekleyin. Sırayı oklarla değiştirebilir, her dosya için sayfa aralığı yazabilirsiniz (boş = tümü).", 12.5).Also(t => t.Margin = new Thickness(4, 10, 4, 10)));
        for (int i = 0; i < _merge.Count; i++)
        {
            var (path, range) = _merge[i];
            int idx = i;
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = $"{i + 1}.", VerticalAlignment = VerticalAlignment.Center });
            var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(Kit.PdfIcon(16).Also(x => x.Margin = new Thickness(0, 0, 8, 0)));
            name.Children.Add(new TextBlock { Text = Path.GetFileName(path), ToolTip = path, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 });
            Grid.SetColumn(name, 1); row.Children.Add(name);
            var rangeBox = new TextBox { Text = range, PlaceholderText = "Tümü", ClearButtonEnabled = false, Margin = new Thickness(6, 0, 6, 0), ToolTip = "Örnek: 1-3, 5" };
            rangeBox.LostFocus += (_, _) => { if (idx < _merge.Count) _merge[idx] = (_merge[idx].Path, rangeBox.Text.Trim()); };
            Grid.SetColumn(rangeBox, 2); row.Children.Add(rangeBox);
            var acts = new StackPanel { Orientation = Orientation.Horizontal };
            acts.Children.Add(Kit.IconButton(SymbolRegular.ArrowUp24, "Yukarı taşı", () => { if (idx > 0) { _merge.Move(idx, idx - 1); Show("merge"); } }));
            acts.Children.Add(Kit.IconButton(SymbolRegular.ArrowDown24, "Aşağı taşı", () => { if (idx < _merge.Count - 1) { _merge.Move(idx, idx + 1); Show("merge"); } }));
            acts.Children.Add(Kit.IconButton(SymbolRegular.Dismiss24, "Listeden kaldır", () => { _merge.RemoveAt(idx); Show("merge"); }));
            Grid.SetColumn(acts, 3); row.Children.Add(acts);
            list.Children.Add(row);
        }
        st.Children.Add(Kit.Card(list));
        var go = Kit.Primary("Birleştir", SymbolRegular.DocumentMultiple24, () => _ = DoMerge());
        go.HorizontalAlignment = HorizontalAlignment.Right; go.Margin = new Thickness(0, 14, 0, 0); go.MinWidth = 140;
        go.IsEnabled = _merge.Count >= 2;
        st.Children.Add(go);
        if (_merge.Count == 1) st.Children.Add(Kit.Muted("Birleştirmek için en az iki dosya ekleyin.").Also(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.Margin = new Thickness(0, 6, 0, 0); }));
        return new ScrollViewer { Content = st, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    /// <summary>Listeyi birleştirir. <paramref name="target"/> null → kayıt yeri sorulur.</summary>
    public async Task<string?> DoMerge(string? target = null, IReadOnlyList<string>? files = null)
    {
        if (_main is null) return null;
        if (files is not null) { _merge.Clear(); foreach (var f in files) _merge.Add((f, "")); }
        if (_merge.Count < 2) return null;
        target ??= FileDialogs.SaveFile("Birleştirilmiş.pdf", FileDialogs.PdfFilter, Path.GetDirectoryName(_merge[0].Path), "Birleştirilen PDF'yi kaydet");
        if (target is null) return null;
        var items = _merge.ToList();
        PdfDocument? merged = null;
        var ok = await _main.RunTaskAsync("Belgeler birleştiriliyor…", async (p, ct) =>
        {
            var sources = new List<MergeSource>();
            foreach (var (path, range) in items)
            {
                string? pw = null;
                using (var probe = await _main.OpenWithPasswordCaptureAsync(path, v => pw = v))
                    if (probe is null) throw new OperationCanceledException();
                sources.Add(new MergeSource(path, pw, string.IsNullOrWhiteSpace(range) ? null : range));
            }
            merged = await PdfTools.MergeAsync(sources, p, ct);
            await merged.SaveAsync(target, ct);
        }, "merge", string.Join("; ", items.Select(i => Path.GetFileName(i.Path))), () => target);
        merged?.Dispose();
        if (!ok) return null;
        _main.StatusText = $"{items.Count} belge birleştirildi: {target}";
        if (DialogBase.Automation is null && MessageDialog.Confirm("Birleştirme tamamlandı", $"{items.Count} belge birleştirildi:\n{target}\n\nAçılsın mı?", "Aç", "Kapat", MessageKind.Success))
            await _main.OpenFilesAsync(new[] { target });
        return target;
    }

    // ─────────────── Böl ───────────────

    private SourcePdf? _splitSource;

    private FrameworkElement SplitView()
    {
        if (_splitSource?.Tab is { } st0 && _main is not null && !_main.Tabs.Contains(st0)) _splitSource = null;
        if (_splitSource is null && _main?.ActiveTab is { } t) _splitSource = SourcePdf.FromTab(t);
        var st = new StackPanel { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var pick = Kit.Secondary(_splitSource is null ? "PDF Seç" : "Değiştir", SymbolRegular.FolderOpen24, async () =>
        {
            var f = FileDialogs.OpenFiles(FileDialogs.PdfFilter, false, "Bölünecek PDF'yi seçin");
            if (f.Length == 0 || _main is null) return;
            var s = await SourcePdf.OpenAsync(_main, f[0]);
            if (s is null) return;
            _splitSource?.Dispose();
            _splitSource = s;
            Show("split");
        });
        DockPanel.SetDock(pick, Dock.Right);
        head.Children.Add(pick);
        var icon = Kit.PdfIcon(26); icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left); head.Children.Add(icon);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = _splitSource?.Name ?? "Bölünecek PDF seçilmedi", FontWeight = FontWeights.SemiBold });
        if (_splitSource is not null) info.Children.Add(Kit.Muted(_splitSource.Info));
        head.Children.Add(info);
        st.Children.Add(Kit.Card(head, new Thickness(14, 10, 10, 10)));
        if (_splitSource is not { } src) return st;

        int pc = src.PageCount;
        var rN = new RadioButton { Content = "Her N sayfada bir dosya", GroupName = "pmSplit", IsChecked = true, Margin = new Thickness(0, 14, 0, 4) };
        var n = Form.Number(Math.Max(1, Math.Min(10, pc / 2)), 1, Math.Max(1, pc)); n.Margin = new Thickness(26, 0, 0, 6); n.HorizontalAlignment = HorizontalAlignment.Left; n.Width = 140;
        var rR = new RadioButton { Content = "Sayfa aralıklarına göre", GroupName = "pmSplit", Margin = new Thickness(0, 4, 0, 4) };
        var ranges = new TextBox { Text = $"1-{Math.Max(1, pc / 2)}; {Math.Min(pc, pc / 2 + 1)}-son", ClearButtonEnabled = false, Margin = new Thickness(26, 0, 0, 2) };
        var rS = new RadioButton { Content = "Her sayfa ayrı dosya", GroupName = "pmSplit", Margin = new Thickness(0, 6, 0, 4) };
        var rO = new RadioButton { Content = "Tek ve çift sayfalar (2 dosya)", GroupName = "pmSplit", Margin = new Thickness(0, 4, 0, 4) };
        var folder = new TextBox { Text = File.Exists(src.Path) ? Path.GetDirectoryName(src.Path)! : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ClearButtonEnabled = false };
        var browse = Kit.Secondary("Gözat…", null, () => { var f = FileDialogs.PickFolder("Bölünen dosyaların klasörü", folder.Text); if (f is not null) folder.Text = f; });
        browse.Margin = new Thickness(8, 0, 0, 0);
        var name = new TextBox { Text = Path.GetFileNameWithoutExtension(src.Name), ClearButtonEnabled = false };
        var preview = Kit.Muted("", 12);
        SplitPlan Plan() => rN.IsChecked == true ? new SplitPlan(SplitMode.EveryNPages, (int)(n.Value ?? 1))
            : rR.IsChecked == true ? new SplitPlan(SplitMode.ByRanges, Ranges: ranges.Text)
            : rS.IsChecked == true ? new SplitPlan(SplitMode.SinglePages) : new SplitPlan(SplitMode.OddEven);
        void Update()
        {
            try { var g = PdfTools.PlanSplit(pc, Plan()); preview.Text = $"{g.Count} dosya oluşturulacak: " + string.Join(", ", g.Take(4).Select(x => PageRange.Format(x))) + (g.Count > 4 ? ", …" : ""); }
            catch (FormatException ex) { preview.Text = ex.Message; }
        }
        foreach (var rb in new[] { rN, rR, rS, rO }) rb.Checked += (_, _) => Update();
        n.ValueChanged += (_, _) => Update();
        ranges.TextChanged += (_, _) => Update();
        Update();
        st.Children.Add(rN); st.Children.Add(n); st.Children.Add(rR); st.Children.Add(ranges);
        st.Children.Add(Kit.Muted("Aralıkları noktalı virgülle ayırın. Örnek: 1-3; 4-10; 11-son").Also(t => t.Margin = new Thickness(26, 0, 0, 4)));
        st.Children.Add(rS); st.Children.Add(rO);
        var fg = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        fg.ColumnDefinitions.Add(new ColumnDefinition()); fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        fg.Children.Add(folder); Grid.SetColumn(browse, 1); fg.Children.Add(browse);
        st.Children.Add(Kit.Field("Hedef klasör:", fg, 120, 14));
        st.Children.Add(Kit.Field("Dosya adı öneki:", name, 120));
        st.Children.Add(preview.Also(t => t.Margin = new Thickness(0, 10, 0, 0)));
        var go = Kit.Primary("Böl", SymbolRegular.Cut24, async () =>
        {
            SplitPlan plan;
            try { plan = Plan(); PdfTools.PlanSplit(pc, plan); }
            catch (FormatException ex) { MessageDialog.Show("Aralık hatalı", ex.Message, MessageKind.Warning); return; }
            await DoSplit(plan, folder.Text.Trim(), name.Text.Trim());
        });
        go.HorizontalAlignment = HorizontalAlignment.Right; go.MinWidth = 140; go.Margin = new Thickness(0, 16, 0, 0);
        st.Children.Add(go);
        return new ScrollViewer { Content = st, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public async Task<IReadOnlyList<string>> DoSplit(SplitPlan plan, string folder, string baseName)
    {
        if (_main is null || _splitSource is null) return Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "Bölüm";
        if (string.IsNullOrWhiteSpace(folder)) { MessageDialog.Show("Klasör seçin", "Bölünen dosyaların kaydedileceği klasörü seçin.", MessageKind.Warning); return Array.Empty<string>(); }
        var src = _splitSource;
        IReadOnlyList<string> files = Array.Empty<string>();
        await _main.RunTaskAsync("Belge bölünüyor…", async (p, ct) =>
        {
            Directory.CreateDirectory(folder);
            files = await PdfTools.SplitAsync(src.Document, PdfTools.PlanSplit(src.PageCount, plan), folder, baseName, p, ct);
        }, "split", src.Name, () => folder);
        if (files.Count > 0)
        {
            _main.StatusText = $"Belge {files.Count} dosyaya bölündü: {folder}";
            if (DialogBase.Automation is null && MessageDialog.Confirm("Bölme tamamlandı", $"Belge {files.Count} dosyaya bölündü.\n{folder}\n\nKlasör açılsın mı?", "Klasörü aç", "Kapat", MessageKind.Success))
                try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); } catch { }
        }
        return files;
    }

    /// <summary>Öz test için.</summary>
    public DocumentViewModel? OrderDocument => _orderDoc;
}
