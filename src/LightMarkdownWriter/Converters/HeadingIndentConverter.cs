using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LightMarkdownWriter.Converters;

/// <summary>Heading level → left margin, so the outline shows the document's structure.</summary>
public sealed class HeadingIndentConverter : IValueConverter
{
    private const double StepPixels = 14;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(value is int level ? Math.Max(0, level - 1) * StepPixels : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
