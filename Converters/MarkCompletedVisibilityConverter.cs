using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TodoApp.ViewModels;

namespace TodoApp.Converters;

// "Mark Completed" doesn't make sense while looking at a task that's already Completed (it's
// already done) or sitting in Trash (status changes there should go through Restore first).
public class MarkCompletedVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is SidebarFilterKind.Done or SidebarFilterKind.Trash ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
