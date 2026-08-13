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
        var window = new ThemedMessageBoxWindow(message, title, button, icon)
        {
            Owner = Application.Current.MainWindow
        };
        window.ShowDialog();
        return window.Result;
    }
}
