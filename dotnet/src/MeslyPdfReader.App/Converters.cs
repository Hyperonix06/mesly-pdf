using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MeslyPdfReader.App;

public sealed class BoolInvertConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) =>
        value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is int i && i == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class PositiveToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is int i && i > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Enum/değer eşitliğini RadioButton.IsChecked'e bağlamak için.</summary>
public sealed class EqualsToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => Equals(value?.ToString(), p?.ToString());
    public object ConvertBack(object value, Type t, object? p, CultureInfo c)
    {
        if (value is true && p is not null)
            return t.IsEnum ? Enum.Parse(t, p.ToString()!) : System.Convert.ChangeType(p, t, CultureInfo.InvariantCulture);
        return Binding.DoNothing;
    }
}

public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => Equals(value?.ToString(), p?.ToString()) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object? p, CultureInfo c) => Binding.DoNothing;
}

public sealed class BoolToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
