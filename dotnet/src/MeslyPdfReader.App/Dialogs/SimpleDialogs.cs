using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace MeslyPdfReader.App.Dialogs;

public enum MessageKind { Info, Warning, Error, Success }
public enum SaveChoice { Save, Discard, Cancel }

public sealed class MessageDialog : DialogBase
{
    public MessageDialog(string title, string message, MessageKind kind, string? details = null) : base(title, 480)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new SymbolIcon
        {
            Symbol = kind switch { MessageKind.Error => SymbolRegular.ErrorCircle24, MessageKind.Warning => SymbolRegular.Warning24, MessageKind.Success => SymbolRegular.CheckmarkCircle24, _ => SymbolRegular.Info24 },
            FontSize = 30, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top,
        };
        icon.SetResourceReference(ForegroundProperty, "MeslyAccentBrush");
        if (kind == MessageKind.Error) icon.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x3B, 0x3B));
        if (kind == MessageKind.Warning) icon.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0x8E, 0x0B));
        grid.Children.Add(icon);
        var stack = new StackPanel();
        stack.Children.Add(Paragraph(message));
        if (!string.IsNullOrWhiteSpace(details))
        {
            var exp = new Expander { Header = "Ayrıntılar", Margin = new Thickness(0, 10, 0, 0) };
            exp.Content = new System.Windows.Controls.TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 11 };
            stack.Children.Add(exp);
        }
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        Body = grid;
    }

    public static void Show(string title, string message, MessageKind kind = MessageKind.Info, string? details = null)
    {
        var d = new MessageDialog(title, message, kind, details);
        d.AddButton("Tamam", () => d.DialogResult = true, primary: true, isDefault: true, isCancel: true);
        d.ShowDialog();
    }

    public static bool Confirm(string title, string message, string yes = "Evet", string no = "Vazgeç", MessageKind kind = MessageKind.Warning)
    {
        var d = new MessageDialog(title, message, kind);
        d.AddButton(no, () => d.DialogResult = false, isCancel: true);
        d.AddButton(yes, () => d.DialogResult = true, primary: true, isDefault: true);
        return d.ShowDialog() == true;
    }

    public static SaveChoice AskSave(string documentName)
    {
        var d = new MessageDialog("Kaydedilmemiş değişiklikler", $"“{documentName}” belgesinde kaydedilmemiş değişiklikler var. Kaydetmek istiyor musunuz?", MessageKind.Warning);
        var choice = SaveChoice.Cancel;
        d.AddButton("İptal", () => { choice = SaveChoice.Cancel; d.DialogResult = false; }, isCancel: true);
        d.AddButton("Kaydetme", () => { choice = SaveChoice.Discard; d.DialogResult = true; });
        d.AddButton("Kaydet", () => { choice = SaveChoice.Save; d.DialogResult = true; }, primary: true, isDefault: true);
        d.ShowDialog();
        return choice;
    }
}

public sealed class InputDialog : DialogBase
{
    private readonly TextBox _box;
    private readonly System.Windows.Controls.TextBlock _err = Error();
    private readonly Func<string, string?>? _validate;
    public string Value => _box.Text;

    public InputDialog(string title, string label, string value, string? hint, Func<string, string?>? validate) : base(title, 440)
    {
        _validate = validate;
        var stack = new StackPanel();
        stack.Children.Add(Label(label, 0));
        _box = new TextBox { Text = value, ClearButtonEnabled = true };
        stack.Children.Add(_box);
        if (hint is not null) stack.Children.Add(Hint(hint));
        stack.Children.Add(_err);
        Body = stack;
        AddOkCancel(validate: () =>
        {
            var e = _validate?.Invoke(_box.Text);
            ShowError(_err, e);
            return e is null;
        });
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    public static string? Ask(string title, string label, string value = "", string? hint = null, Func<string, string?>? validate = null)
    {
        var d = new InputDialog(title, label, value, hint, validate);
        return d.ShowDialog() == true ? d.Value : null;
    }
}

public sealed class PasswordDialog : DialogBase
{
    private readonly PasswordBox _box;
    public PasswordDialog(string fileName, bool wrong) : base("Parola gerekli", 440)
    {
        var stack = new StackPanel();
        stack.Children.Add(Paragraph($"“{fileName}” parola ile korunuyor. Açmak için parolayı girin."));
        stack.Children.Add(Label("Parola"));
        _box = new PasswordBox();
        stack.Children.Add(_box);
        if (wrong)
        {
            var e = Error();
            ShowError(e, "Girilen parola yanlış. Tekrar deneyin.");
            stack.Children.Add(e);
        }
        Body = stack;
        AddOkCancel("Aç");
        Loaded += (_, _) => _box.Focus();
    }

    public static string? Ask(string fileName, bool wrong)
    {
        var d = new PasswordDialog(fileName, wrong);
        return d.ShowDialog() == true ? d._box.Password : null;
    }
}
