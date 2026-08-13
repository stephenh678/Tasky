using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TodoApp.Converters;

/// <summary>
/// True collapses the column to 0; False restores it to the width given in ConverterParameter (a pixel value or "Auto").
/// </summary>
public class BoolToGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not bool collapse) return GridLength.Auto;
        if (collapse) return new GridLength(0);

        var text = parameter as string ?? "Auto";
        if (text.Equals("Auto", StringComparison.OrdinalIgnoreCase)) return GridLength.Auto;
        return double.TryParse(text, out var pixels) ? new GridLength(pixels) : GridLength.Auto;
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
