using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MeslyPdfReader.App.ViewModels;

namespace MeslyPdfReader.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        DragEnter += (_, _) => DropZone.SetResourceReference(Border.BorderBrushProperty, "MeslyAccentBrush");
        DragLeave += (_, _) => DropZone.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
        Drop += (_, _) => DropZone.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
        SizeChanged += (_, e) => Actions.Columns = e.NewSize.Width < 900 ? 2 : e.NewSize.Width < 1250 ? 3 : 4;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void Recent_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecentList.SelectedItem is RecentItem r) Vm?.OpenRecentCommand.Execute(r);
    }

    private void Recent_KeyDown(object sender, KeyEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentItem r) return;
        if (e.Key == Key.Enter) { Vm?.OpenRecentCommand.Execute(r); e.Handled = true; }
        else if (e.Key == Key.Delete) { Vm?.RemoveRecentCommand.Execute(r); e.Handled = true; }
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e) => Vm?.OpenCommand.Execute(null);
}
