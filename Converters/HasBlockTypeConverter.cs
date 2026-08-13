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
        if (value is not IEnumerable<NoteBlock> blocks) return Visibility.Collapsed;
        if (parameter is not string typeName || !Enum.TryParse<NoteBlockType>(typeName, out var type)) return Visibility.Collapsed;
        return blocks.Any(b => b.Type == type) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
