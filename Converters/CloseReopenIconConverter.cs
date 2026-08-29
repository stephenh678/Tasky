using System;
using System.Globalization;
using System.Windows.Data;

namespace TodoApp.Converters;

public class CloseReopenIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "↩" : "\U0001F5D1";

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
