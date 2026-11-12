using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeslyPdfReader.App.ViewModels;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace MeslyPdfReader.App.Views.Screens;

public partial class DocumentsScreen : UserControl
{
    public DocumentsScreen()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && Main is { } m) _ = m.Library.RefreshAsync(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F5 && Main is { } m) { _ = m.Library.RefreshAsync(); e.Handled = true; } };
    }

    private MainViewModel? Main => DataContext as MainViewModel;

    private void Recent_Click(object sender, RoutedEventArgs e) { if (Main is { } m) m.Library.Tab = LibraryTab.Recent; }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is LibraryItem it && Main is { } m) m.Library.OpenCommand.Execute(it);
    }

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && List.SelectedItem is LibraryItem it && Main is { } m) { m.Library.OpenCommand.Execute(it); e.Handled = true; }
        else if (e.Key == Key.Delete && List.SelectedItem is LibraryItem d && Main is { } m2) { m2.Library.RemoveCommand.Execute(d); e.Handled = true; }
    }

    /// <summary>Satır menüsü (…): aç, yeni pencerede aç, dönüştür, OCR, klasörde göster, sık kullanılan, listeden kaldır.</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibraryItem it } fe || Main is not { } m) return;
        var lib = m.Library;
        MenuItem Mi(string h, SymbolRegular icon, System.Windows.Input.ICommand cmd) =>
            new() { Header = h, Command = cmd, CommandParameter = it, Icon = new SymbolIcon { Symbol = icon, FontSize = 16 } };
        var menu = new ContextMenu();
        menu.Items.Add(Mi("Aç", SymbolRegular.Open24, lib.OpenCommand));
        menu.Items.Add(Mi("Yeni pencerede aç", SymbolRegular.WindowNew24, lib.OpenInNewWindowCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mi("Dönüştür…", SymbolRegular.ArrowSync24, lib.ConvertItemCommand));
        menu.Items.Add(Mi("Metni tanı (OCR)…", SymbolRegular.ScanText24, lib.OcrItemCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Mi(it.Pinned ? "Sık kullanılanlardan çıkar" : "Sık kullanılanlara ekle", SymbolRegular.Star24, lib.TogglePinCommand));
        menu.Items.Add(Mi("Dosya konumunu aç", SymbolRegular.FolderOpen24, lib.ShowInFolderCommand));
        menu.Items.Add(Mi("Listeden kaldır", SymbolRegular.Delete24, lib.RemoveCommand));
        menu.PlacementTarget = fe;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
