using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using TodoApp.Behaviors;
using TodoApp.Models;

namespace TodoApp.Converters;

public class NoteBlockToPhotoSourceConverter : IValueConverter
{
    // ROADMAP #65: WPF's binding engine calls Convert() on every list/editor re-render (a title
    // keystroke, search, a sync refresh), and this used to decode a brand-new BitmapImage from
    // disk every single time even when the underlying file hadn't changed. Attachments get a
    // random UUID filename on creation and are never edited in place, so a path's content is
    // effectively immutable once written - cached by path + LastWriteTimeUtc (belt-and-suspenders
    // against a 3-way-diff sync replacing a file at the same path), same cap-then-prune-oldest
    // shape as Tasky Web's #70 thumbnail cache. Declared once as a shared XAML resource
    // (MainWindow.xaml's "NoteBlockToPhotoSource" key), so this instance-level cache lives for the
    // life of the running app. Safe to share the frozen BitmapImage instance across every control
    // bound to the same block - Freeze() makes it immutable.
    private const int MaxCacheEntries = 200;
    private readonly Dictionary<string, (DateTime LastWriteUtc, BitmapImage Bitmap)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _cacheOrder = new();

    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not NoteBlock block) return null;
        var path = RichTextBoxBehavior.ResolveLocalAttachmentPath(block);
        if (path is null) return null;

        try
        {
            var lastWriteUtc = File.GetLastWriteTimeUtc(path);
            if (_cache.TryGetValue(path, out var cached) && cached.LastWriteUtc == lastWriteUtc)
                return cached.Bitmap;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            if (!_cache.ContainsKey(path))
            {
                _cacheOrder.Add(path);
                if (_cacheOrder.Count > MaxCacheEntries)
                {
                    var oldest = _cacheOrder[0];
                    _cacheOrder.RemoveAt(0);
                    _cache.Remove(oldest);
                }
            }
            _cache[path] = (lastWriteUtc, bitmap);
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
