using System.Windows;
using System.Windows.Input;
using TodoApp.Services;

namespace TodoApp;

public partial class SaveViewPromptWindow : Window
{
    public string? NameResult { get; private set; }

    public SaveViewPromptWindow()
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            NameResult = name;
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
            Save_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
        }
    }
}
