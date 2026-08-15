using System.Windows;
using System.Windows.Input;
using TodoApp.Services;

namespace TodoApp;

public partial class LinkPromptWindow : Window
{
    public string? UrlResult { get; private set; }

    public LinkPromptWindow()
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        Loaded += (_, _) =>
        {
            UrlBox.Focus();
            UrlBox.SelectAll();
        };
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (!string.IsNullOrEmpty(url) && url != "https://")
        {
            UrlResult = url;
            DialogResult = true;
        }
        else
        {
            DialogResult = false;
        }
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Insert_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
        }
    }
}
