using System.Windows;

namespace TodoApp;

// A native MessageBox is drawn by the OS, entirely outside WPF's theming - it stays whatever
// color the OS's own light/dark app mode happens to be, ignoring the app's own theme choice
// completely. This is a themed drop-in replacement with the same call signature.
public static class ThemedMessageBox
{
    public static MessageBoxResult Show(string message, string title,
        MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        var window = new ThemedMessageBoxWindow(message, title, button, icon);

        // WPF throws InvalidOperationException if Owner is set to a Window that hasn't been
        // shown yet - true for the common case, but not for an error dialog triggered by an
        // exception during startup, before MainWindow.Show() has run. Fall back to unowned
        // (centered on screen) rather than crashing the crash handler itself.
        if (Application.Current.MainWindow is { IsLoaded: true } owner)
            window.Owner = owner;

        window.ShowDialog();
        return window.Result;
    }
}
