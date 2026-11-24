using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Grably.App.ViewModels;

/// <summary>Small value converters used from XAML with x:Static.</summary>
public static class Converters
{
    /// <summary>Visible when the bound number is greater than zero.</summary>
    public static readonly IValueConverter PositiveToVisible = new Converter(v => v is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed);

    private sealed class Converter(Func<object?, object> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
