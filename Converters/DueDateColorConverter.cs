using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace TodoApp.Converters;

public class DueDateColorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var normal = FindBrush("ForegroundBrush", Color.FromRgb(0x64, 0x69, 0x70));

        if (values.Length < 2 || values[0] is not DateTime due || values[1] is not bool isDone)
            return normal;

        if (isDone) return normal;

        var today = DateTime.Today;
        if (due.Date < today) return FindBrush("OverdueBrush", Color.FromRgb(0xD6, 0x36, 0x38));
        if (due.Date == today) return FindBrush("WarningBrush", Color.FromRgb(0xFA, 0xA7, 0x54));
        return normal;
    }

    private static Brush FindBrush(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
