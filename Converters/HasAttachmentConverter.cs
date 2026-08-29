using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using TodoApp.Models;

namespace TodoApp.Converters;

public class HasAttachmentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TaskItem task)
        {
            var kind = parameter as string ?? "All";
            var has = kind switch
            {
                "Photo" => TaskMediaHelper.HasPhoto(task),
                "Attachment" or "File" => TaskMediaHelper.HasAttachment(task),
                _ => TaskMediaHelper.HasAttachment(task) || TaskMediaHelper.HasPhoto(task)
            };
            return has ? Visibility.Visible : Visibility.Collapsed;
        }

        if (value is IEnumerable<NoteBlock> blocks)
        {
            var taskWrapper = new TaskItem { Body = new System.Collections.ObjectModel.ObservableCollection<NoteBlock>(blocks) };
            var kind = parameter as string ?? "All";
            var has = kind switch
            {
                "Photo" => TaskMediaHelper.HasPhoto(taskWrapper),
                "Attachment" or "File" => TaskMediaHelper.HasAttachment(taskWrapper),
                _ => TaskMediaHelper.HasAttachment(taskWrapper) || TaskMediaHelper.HasPhoto(taskWrapper)
            };
            return has ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
