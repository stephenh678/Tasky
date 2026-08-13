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
        => value is IEnumerable<NoteBlock> blocks && blocks.Any(b => b.Type is NoteBlockType.Photo or NoteBlockType.File)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
