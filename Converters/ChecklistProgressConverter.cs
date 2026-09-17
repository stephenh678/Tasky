using System;
using System.Globalization;
using System.Windows.Data;
using TodoApp.Models;

namespace TodoApp.Converters;

public class ChecklistProgressTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TaskItem task) return string.Empty;
        var (completed, total) = TaskMediaHelper.GetChecklistProgress(task);
        if (total == 0) return string.Empty;
        return $"{completed}/{total}";
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class ChecklistProgressToolTipConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TaskItem task) return "Has checklist";
        var (completed, total) = TaskMediaHelper.GetChecklistProgress(task);
        if (total == 0) return "Has checklist";
        return $"{completed} of {total} subtasks completed";
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
