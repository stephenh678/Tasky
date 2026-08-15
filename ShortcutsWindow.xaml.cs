using System.Windows;
using TodoApp.Services;

namespace TodoApp;

public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
