using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using TodoApp.Models;

namespace TodoApp.Converters;

public class HasBlockTypeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var typeName = parameter as string ?? string.Empty;

        TaskItem? task = value as TaskItem;
        if (task is null && value is IEnumerable<NoteBlock> blocks)
        {
            task = new TaskItem { Body = new System.Collections.ObjectModel.ObservableCollection<NoteBlock>(blocks) };
        }

        if (task is null) return Visibility.Collapsed;

        var has = typeName.ToLowerInvariant() switch
        {
            "photo" or "image" => TaskMediaHelper.HasPhoto(task),
            "file" or "attachment" => TaskMediaHelper.HasAttachment(task),
            "link" or "url" => TaskMediaHelper.HasLink(task),
            "checklist" or "checkbox" => TaskMediaHelper.HasChecklist(task),
            _ => false
        };

        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
