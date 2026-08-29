using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TodoApp.Models;

namespace TodoApp.Converters;

public class PriorityColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var priority = value as TaskPriority? ?? TaskPriority.None;
        return priority switch
        {
            TaskPriority.High => FindBrush("DangerBrush", Color.FromRgb(0xD6, 0x36, 0x38)),
            TaskPriority.Medium => FindBrush("WarningBrush", Color.FromRgb(0xB4, 0x53, 0x09)),
            TaskPriority.Low => FindBrush("AccentBrush", Color.FromRgb(0x33, 0x61, 0xCC)),
            _ => Brushes.Transparent
        };
    }

    private static Brush FindBrush(string key, Color fallback)
        => Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
