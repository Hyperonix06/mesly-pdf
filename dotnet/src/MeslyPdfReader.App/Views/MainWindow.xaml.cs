using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MeslyPdfReader.App.Controls;
using MeslyPdfReader.App.Views;
using MeslyPdfReader.App.Services;
using MeslyPdfReader.App.ViewModels;
using MeslyPdfReader.Data;

namespace MeslyPdfReader.App.Views;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MainViewModel _vm;
    private bool _closingConfirmed;
    private WindowState _stateBeforeFullScreen;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        SizeChanged += (_, _) => ApplyUiScale();
        vm.UiScaleChanged += ApplyUiScale;
        RenderOptions.SetBitmapScalingMode(Shell, BitmapScalingMode.HighQuality);
    }

    /// <summary>
    /// Arayüz ölçeği. Afişteki ekranlar standart Windows boyutundan iri ve pencereyi dolduran bir düzen gösterir; otomatik
    /// kipte ekranlar pencere boyutuna göre orantılı büyütülür (tasarım tabanı 830×640). PDF sayfası ölçeklenmez.
    /// </summary>
    public static double ComputeUiScale(int setting, double width, double height)
    {
        if (setting >= 80) return setting / 100.0;
        if (width <= 0 || height <= 0) return 1.3;
        return Math.Clamp(Math.Min(width / 830.0, (height - 64) / 640.0), 1.0, 1.75);
    }

    public double UiScale { get; private set; } = 1;

    /// <summary>Görüntüleyici çerçevesinin (şerit, kenar panelleri) ölçeği.</summary>
    public static double ChromeScale { get; private set; } = 1;
    public static event Action? ChromeScaleChanged;

    private void ApplyUiScale()
    {
        double s = ComputeUiScale(_vm.Settings.UiScale, ActualWidth, ActualHeight);
        if (Math.Abs(s - UiScale) < 0.005 && Shell.LayoutTransform is ScaleTransform) return;
        UiScale = s;
        Shell.LayoutTransform = new ScaleTransform(s, s);
        // Görüntüleyicide şerit ve sekmeler de büyür; şerit dar pencerede taşmasın diye en çok 1,3.
        double r = Math.Min(s, 1.3);
        if (Math.Abs(r - ChromeScale) > 0.005) { ChromeScale = r; ChromeScaleChanged?.Invoke(); }
        var rt = new ScaleTransform(r, r);
        RibbonTabs.LayoutTransform = rt; RibbonBody.LayoutTransform = rt; DocTabs.LayoutTransform = rt; StatusBar.LayoutTransform = rt;
    }

    public MainViewModel ViewModel => _vm;

    /// <summary>Öz test: kapanışta kaydetme sorusu sorulmasın.</summary>
    public bool SkipExitPrompt { get => _closingConfirmed; set => _closingConfirmed = value; }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        Activate();
        await _vm.OpenFilesAsync(files);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_closingConfirmed || _vm.Tabs.Count > 0) _vm.SaveSession();
        if (_closingConfirmed) return;
        if (_vm.Tabs.All(t => !t.Document.IsModified))
        {
            foreach (var t in _vm.Tabs.ToList()) { _vm.Recovery.Forget(t); t.Dispose(); }
            return;
        }
        e.Cancel = true;
        if (await _vm.PrepareExitAsync())
        {
            _closingConfirmed = true;
            Close();
        }
    }

    // ─────────────── Klavye kısayolları ───────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var doc = _vm.ActiveTab;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        bool inText = Keyboard.FocusedElement is TextBox or PasswordBox;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        bool Run(Action a) { a(); e.Handled = true; return true; }
        bool Exec(System.Windows.Input.ICommand? c, object? p = null) { if (c?.CanExecute(p) == true) c.Execute(p); e.Handled = true; return true; }

        if (key == Key.F11 && doc is not null) { Run(ToggleFullScreen); return; }
        if (key == Key.Escape && _vm.IsFullScreen) { Run(ToggleFullScreen); return; }
        // Esc: düzenleme kipinde seçim/yerleştirme yoksa kipten çık (seçim varsa önce görüntüleyici seçimi kaldırır).
        if (key == Key.Escape && _vm.IsViewer && _vm.IsEditMode && !inText && doc is not null && doc.Placement == EditPlacement.None && !doc.HasEditSelection
            && doc.Tool is ViewerTool.Edit or ViewerTool.Select)
        { Run(() => _vm.SetEditMode(false)); return; }
        if (key == Key.F5 && _vm.Screen == AppScreen.Documents) { Run(() => _ = _vm.Library.RefreshAsync()); return; }

        if (ctrl)
        {
            switch (key)
            {
                case Key.O: Exec(_vm.OpenCommand); return;
                case Key.N when !shift: Exec(_vm.CreateBlankCommand); return; // FineReader: Ctrl+N yeni PDF
                case Key.Tab: Run(() => CycleTab(shift ? -1 : 1)); return;
                case Key.W when doc is not null: Exec(_vm.CloseTabCommand, doc); return;
            }
            if (doc is null) return;
            switch (key)
            {
                case Key.S when shift: Exec(doc.SaveAsCommand); return;
                case Key.S: Exec(doc.SaveCommand); return;
                case Key.P: Exec(doc.PrintCommand); return;
                case Key.F: Run(FocusSearch); return;
                case Key.G: Exec(doc.GoToPagePromptCommand); return;
                case Key.E when !inText: Run(() => _vm.SetEditMode(true)); return; // FineReader: Ctrl+E İçeriği Düzenle
                case Key.D: Exec(doc.AddBookmarkCommand); return;
                case Key.R when shift: Exec(doc.RotatePagesLeftCommand); return;
                case Key.R: Exec(doc.RotatePagesRightCommand); return;
                case Key.N when shift: Exec(doc.ToggleDarkCommand); return;
                case Key.OemPlus or Key.Add: Exec(doc.ZoomInCommand); return;
                case Key.OemMinus or Key.Subtract: Exec(doc.ZoomOutCommand); return;
                case Key.D0 or Key.NumPad0: Exec(doc.FitPageCommand); return;
                case Key.D1 or Key.NumPad1: Exec(doc.ActualSizeCommand); return;
                case Key.D2 or Key.NumPad2: Exec(doc.FitWidthCommand); return;
            }
            if (!inText)
            {
                switch (key)
                {
                    case Key.Z: Exec(doc.UndoCommand); return;
                    case Key.Y: Exec(doc.RedoCommand); return;
                }
            }
            return;
        }
        if (doc is null) return;
        if (key == Key.F3) { Exec(shift ? doc.PreviousHitCommand : doc.NextHitCommand); return; }
        if (inText) return;
        switch (key)
        {
            case Key.H when mods == ModifierKeys.None: doc.Tool = ViewerTool.Hand; e.Handled = true; return;
            case Key.V when mods == ModifierKeys.None: doc.Tool = ViewerTool.Select; e.Handled = true; return;
            case Key.E when mods == ModifierKeys.None:
                _vm.SetEditMode(true); e.Handled = true; return;
        }
    }

    private void CycleTab(int dir)
    {
        if (_vm.Tabs.Count == 0) return;
        if (!_vm.IsViewer) { _vm.ShowDocumentCommand.Execute(_vm.ActiveTab ?? _vm.Tabs[0]); return; }
        int i = _vm.ActiveTab is null ? 0 : _vm.Tabs.IndexOf(_vm.ActiveTab);
        _vm.ShowDocumentCommand.Execute(_vm.Tabs[(i + dir + _vm.Tabs.Count) % _vm.Tabs.Count]);
    }

    /// <summary>Şeritteki “Sayfa” kutusu: Enter → sayfaya git, Esc → geri al.</summary>
    private void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || _vm.ActiveTab is not { } doc) return;
        if (e.Key == Key.Enter)
        {
            tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            tb.Text = doc.PageNumber.ToString();
            doc.Viewer?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            tb.Text = doc.PageNumber.ToString();
            doc.Viewer?.Focus();
            e.Handled = true;
        }
    }

    /// <summary>Ayarlar ekranı (Yardım'dan kısayol listesine geçiş için).</summary>
    public Screens.SettingsScreen SettingsScreenControl => SettingsScreen;

    // ─────────────── Arama / tam ekran / tema ───────────────

    private void FocusSearch()
    {
        if (_vm.ActiveTab is null) return;
        if (_vm.IsFullScreen) ToggleFullScreen();
        QuickSearch.Focus();
        QuickSearch.SelectAll();
    }

    private void FocusSearch_Click(object sender, RoutedEventArgs e) => FocusSearch();

    private void QuickSearch_KeyDown(object sender, KeyEventArgs e)
    {
        var doc = _vm.ActiveTab;
        if (doc is null) return;
        if (e.Key == Key.Enter)
        {
            if (doc.SearchHits is not null && doc.SearchResults.Count > 0 && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                // Aynı sorgu için Enter → sonraki sonuç
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) doc.PreviousHitCommand.Execute(null); else doc.NextHitCommand.Execute(null);
            }
            else doc.SearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            doc.ClearSearchCommand.Execute(null);
            doc.Viewer?.Focus();
            e.Handled = true;
        }
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Şeritteki açılır menülü düğmeler: tıklayınca menüyü düğmenin altında açar.</summary>
    private void OpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } fe)
        {
            menu.DataContext = fe.DataContext;
            menu.PlacementTarget = fe;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void Ribbon_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (RibbonScroll.ScrollableWidth <= 0) return;
        RibbonScroll.ScrollToHorizontalOffset(RibbonScroll.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    public void ToggleFullScreen()
    {
        bool full = !_vm.IsFullScreen;
        _vm.IsFullScreen = full;
        var vis = full ? Visibility.Collapsed : Visibility.Visible;
        TitleBar.Visibility = RibbonTabs.Visibility = RibbonBody.Visibility = DocTabs.Visibility = StatusBar.Visibility = vis;
        if (full)
        {
            _stateBeforeFullScreen = WindowState;
            WindowStyle = WindowStyle.None;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // görev çubuğunu da kapla
            WindowState = WindowState.Maximized;
            _vm.ActiveTab?.SetFullScreenPanels(true);
            _vm.ActiveTab?.Viewer?.Focus();
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullScreen;
            _vm.ActiveTab?.SetFullScreenPanels(false);
        }
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Services.GetService(typeof(AppSettings)) as AppSettings;
        var next = _vm.Theme.IsDark ? ThemeChoice.Light : ThemeChoice.Dark;
        if (settings is not null) settings.Theme = next;
        _vm.Theme.Apply(next);
    }

    // ─────────────── Sekmeler ───────────────

    private void DocTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DocumentViewModel d }) _vm.ActiveTab = d;
    }

    private void DocTab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: DocumentViewModel d })
        {
            _vm.CloseTabCommand.Execute(d);
            e.Handled = true;
        }
    }
}
