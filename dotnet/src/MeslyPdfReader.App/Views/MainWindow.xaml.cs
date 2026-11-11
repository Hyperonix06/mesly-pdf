using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeslyPdfReader.App.Controls;
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

        if (ctrl)
        {
            switch (key)
            {
                case Key.O: Exec(_vm.OpenCommand); return;
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
            case Key.E when mods == ModifierKeys.None: _vm.Ribbon = RibbonTab.Edit; doc.Tool = ViewerTool.Edit; e.Handled = true; return;
        }
    }

    private void CycleTab(int dir)
    {
        var list = new List<DocumentViewModel?> { null };
        list.AddRange(_vm.Tabs);
        int i = list.IndexOf(_vm.ActiveTab);
        _vm.ActiveTab = list[(i + dir + list.Count) % list.Count];
    }

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
