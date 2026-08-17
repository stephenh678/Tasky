using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using TodoApp.Behaviors;
using TodoApp.Models;

namespace TodoApp.Converters;

public class NoteBlockToPhotoSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not NoteBlock block) return null;
        var path = RichTextBoxBehavior.ResolveLocalAttachmentPath(block);
        if (path is null) return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
