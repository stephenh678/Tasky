using System;
using System.Globalization;
using System.Windows.Data;
using TodoApp.ViewModels;

namespace TodoApp.Converters;

public class QuickFilterLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is QuickFilter filter ? filter.Label() : string.Empty;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
