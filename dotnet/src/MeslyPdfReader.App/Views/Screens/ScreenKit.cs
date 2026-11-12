using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MeslyPdfReader.App.Views.Screens;

/// <summary>Kodla kurulan ekranlar için afiş tasarım dilindeki ortak parçalar.</summary>
internal static class Kit
{
    public static TextBlock Title(string text)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(FrameworkElement.StyleProperty, "ScreenTitle");
        return t;
    }

    public static TextBlock Section(string text, double top = 14) => new() { Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, top, 0, 8) };

    public static TextBlock Muted(string text, double size = 12)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MeslyMutedTextBrush");
        return t;
    }

    public static Border Card(UIElement child, Thickness? padding = null)
    {
        var b = new Border { Child = child };
        b.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        if (padding is { } p) b.Padding = p;
        return b;
    }

    private static object Content(string text, SymbolRegular? icon, double iconSize = 15)
    {
        if (icon is null) return text;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new SymbolIcon { Symbol = icon.Value, FontSize = iconSize, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    public static System.Windows.Controls.Button Primary(string text, SymbolRegular? icon = null, Action? click = null)
    {
        var b = new System.Windows.Controls.Button { Content = Content(text, icon) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton");
        if (click is not null) b.Click += (_, _) => click();
        return b;
    }

    public static System.Windows.Controls.Button Secondary(string text, SymbolRegular? icon = null, Action? click = null)
    {
        var b = new System.Windows.Controls.Button { Content = Content(text, icon) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "SecondaryButton");
        if (click is not null) b.Click += (_, _) => click();
        return b;
    }

    public static System.Windows.Controls.Button IconButton(SymbolRegular icon, string tip, Action click, double size = 16)
    {
        var b = new System.Windows.Controls.Button { Content = new SymbolIcon { Symbol = icon, FontSize = size }, ToolTip = tip, Width = 32, Height = 32 };
        b.SetResourceReference(FrameworkElement.StyleProperty, "ToolButtonStyle");
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>“Etiket:  [denetim]” satırı (sabit etiket genişliği).</summary>
    public static Grid Field(string label, UIElement control, double labelWidth = 110, double top = 6)
    {
        var g = new Grid { Margin = new Thickness(0, top, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var l = new TextBlock { Text = label };
        l.SetResourceReference(FrameworkElement.StyleProperty, "FieldLabel");
        g.Children.Add(l);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    public static System.Windows.Controls.CheckBox Check(string text, bool value, Action<bool> set, string? tip = null)
    {
        var c = new System.Windows.Controls.CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 6, 0, 0), ToolTip = tip };
        c.Checked += (_, _) => set(true);
        c.Unchecked += (_, _) => set(false);
        return c;
    }

    public static ComboBox Combo(IEnumerable<string> items, int selected, Action<int>? set = null)
    {
        var c = new ComboBox { MinWidth = 180 };
        foreach (var i in items) c.Items.Add(i);
        c.SelectedIndex = Math.Clamp(selected, 0, Math.Max(0, c.Items.Count - 1));
        if (set is not null) c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) set(c.SelectedIndex); };
        return c;
    }

    public static ToggleButton Segment(string text, string group)
    {
        var r = new System.Windows.Controls.RadioButton { Content = text, GroupName = group };
        r.SetResourceReference(FrameworkElement.StyleProperty, "SegmentTab");
        return r;
    }

    /// <summary>Kırmızı PDF simgesi (dosya satırları için).</summary>
    public static SymbolIcon PdfIcon(double size = 18)
    {
        var i = new SymbolIcon { Symbol = SymbolRegular.DocumentPdf24, FontSize = size, Filled = true };
        i.SetResourceReference(Control.ForegroundProperty, "MeslyAccentBrush");
        return i;
    }

    public static Border Divider(double top = 12, double bottom = 12)
    {
        var b = new Border { Height = 1, Margin = new Thickness(0, top, 0, bottom) };
        b.SetResourceReference(Border.BackgroundProperty, "MeslyBorderBrush");
        return b;
    }

    /// <summary>Ekran kökü: başlık + kaydırılabilir içerik.</summary>
    public static Grid Root(string title, UIElement body, UIElement? headerRight = null)
    {
        var root = new Grid { Margin = new Thickness(28, 20, 28, 20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var head = new DockPanel();
        if (headerRight is not null) { DockPanel.SetDock(headerRight, Dock.Right); head.Children.Add(headerRight); }
        head.Children.Add(Title(title));
        root.Children.Add(head);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        return root;
    }

    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}

internal static class UiExt
{
    public static T WithResource<T>(this T e, DependencyProperty p, string key) where T : FrameworkElement { e.SetResourceReference(p, key); return e; }
}
