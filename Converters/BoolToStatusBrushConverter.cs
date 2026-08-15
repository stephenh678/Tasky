using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TodoApp.Converters;

public class BoolToStatusBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ConnectedBrush = new(Color.FromRgb(76, 175, 80)); // #4CAF50 Green
    private static readonly SolidColorBrush DisconnectedBrush = new(Color.FromRgb(158, 158, 158)); // #9E9E9E Gray

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isTrue && isTrue)
            return ConnectedBrush;
        return DisconnectedBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
