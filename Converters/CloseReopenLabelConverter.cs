using System;
using System.Globalization;
using System.Windows.Data;

namespace TodoApp.Converters;

public class CloseReopenLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "Restore" : "Move to Trash";

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
