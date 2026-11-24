using System.Globalization;
using System.Windows.Data;

namespace LightMarkdownWriter.Converters;

/// <summary>Binds a group of RadioButtons to one enum property: checked when value == parameter.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value, parameter);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? parameter : Binding.DoNothing;
}
