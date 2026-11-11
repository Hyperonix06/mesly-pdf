using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MeslyPdfReader.App.Dialogs;

/// <summary>Tüm Mesly diyaloglarının ortak penceresi: başlık çubuğu, içerik, alt düğme şeridi.</summary>
public class DialogBase : FluentWindow
{
    private readonly Grid _root;
    private readonly ContentControl _body;
    protected readonly StackPanel Footer;

    public DialogBase(string title, double width = 460)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 900;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowCornerPreference = WindowCornerPreference.Round;
        SetResourceReference(BackgroundProperty, "MeslyPanelBrush");
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        if (owner is not null && owner != this && owner.IsLoaded) Owner = owner;

        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var tb = new TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false, CanMaximize = false };
        _root.Children.Add(tb);

        _body = new ContentControl { Margin = new Thickness(24, 8, 24, 16), Focusable = false };
        Grid.SetRow(_body, 1);
        _root.Children.Add(_body);

        var footerBorder = new Border { Padding = new Thickness(24, 14, 24, 14), BorderThickness = new Thickness(0, 1, 0, 0) };
        footerBorder.SetResourceReference(Border.BackgroundProperty, "MeslyChromeBrush");
        footerBorder.SetResourceReference(Border.BorderBrushProperty, "MeslyBorderBrush");
        Footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footerBorder.Child = Footer;
        Grid.SetRow(footerBorder, 2);
        _root.Children.Add(footerBorder);
        base.Content = _root;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
        if (Automation is { } sink)
        {
            // Öz test: beklenmeyen diyaloglar takılmaya yol açmasın; kaydedilip kapatılır.
            Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                sink($"{Title}: {DescribeBody(_body.Content)}");
                try { DialogResult = false; } catch { Close(); }
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>Öz test kipinde açılan her diyalog bu geri çağırmaya bildirilir ve kapatılır.</summary>
    public static Action<string>? Automation { get; set; }

    private static string DescribeBody(object? o)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(DependencyObject? d)
        {
            if (d is null) return;
            if (d is TextBlock t && !string.IsNullOrWhiteSpace(t.Text)) sb.Append(t.Text).Append(' ');
            foreach (var c in LogicalTreeHelper.GetChildren(d)) if (c is DependencyObject dc) Walk(dc);
        }
        Walk(o as DependencyObject);
        return sb.ToString().Trim();
    }

    public object Body { get => _body.Content; set => _body.Content = value; }

    protected Button AddButton(string text, Action onClick, bool primary = false, bool isCancel = false, bool isDefault = false)
    {
        var b = new Button
        {
            Content = text, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0),
            Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary,
            IsCancel = isCancel, IsDefault = isDefault,
        };
        b.Click += (_, _) => onClick();
        Footer.Children.Add(b);
        return b;
    }

    protected void AddOkCancel(string okText = "Tamam", Func<bool>? validate = null)
    {
        AddButton("İptal", () => DialogResult = false, isCancel: true);
        AddButton(okText, () => { if (validate?.Invoke() ?? true) DialogResult = true; }, primary: true, isDefault: true);
    }

    // ── Küçük yardımcılar: kod ile form kurmak için ──
    public static TextBlock Label(string text, double top = 10) => new() { Text = text, Margin = new Thickness(0, top, 0, 4), FontSize = 12.5, TextWrapping = TextWrapping.Wrap };

    public static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20 };

    public static TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MeslyMutedTextBrush");
        return t;
    }

    public static TextBlock Error()
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0x3B, 0x3B)), Visibility = Visibility.Collapsed };
        return t;
    }

    public static void ShowError(TextBlock t, string? msg)
    {
        t.Text = msg ?? "";
        t.Visibility = string.IsNullOrEmpty(msg) ? Visibility.Collapsed : Visibility.Visible;
    }
}
