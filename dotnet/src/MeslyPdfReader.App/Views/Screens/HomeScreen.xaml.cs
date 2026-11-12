using System.Windows;
using System.Windows.Controls;
using MeslyPdfReader.App.ViewModels;

namespace MeslyPdfReader.App.Views.Screens;

public partial class HomeScreen : UserControl
{
    public HomeScreen()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && DataContext is MainViewModel m) m.Library.RefreshStats(); };
        DragEnter += (_, _) => DropZone.SetResourceReference(Border.BorderBrushProperty, "MeslyAccentBrush");
        DragLeave += (_, _) => DropZone.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
        Drop += (_, _) => DropZone.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
    }

    private void Recent_Click(object sender, RoutedEventArgs e) => Go(LibraryTab.Recent);
    private void Favorites_Click(object sender, RoutedEventArgs e) => Go(LibraryTab.Favorites);

    private void Go(LibraryTab tab)
    {
        if (DataContext is not MainViewModel m) return;
        m.Library.Tab = tab;
        m.Navigate(AppScreen.Documents);
    }
}
