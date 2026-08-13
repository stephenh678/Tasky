using System.Windows;
using TodoApp.Services;

namespace TodoApp;

public partial class ThemedMessageBoxWindow : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    public ThemedMessageBoxWindow(string message, string title, MessageBoxButton button, MessageBoxImage icon)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        Title = title;
        MessageText.Text = message;

        IconText.Text = icon switch
        {
            MessageBoxImage.Warning => "⚠",
            MessageBoxImage.Question => "❓",
            MessageBoxImage.Information => "ℹ",
            MessageBoxImage.Error => "⛔",
            _ => string.Empty
        };
        IconText.Visibility = IconText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        YesButton.Visibility = Visibility.Collapsed;
        NoButton.Visibility = Visibility.Collapsed;
        OkButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Collapsed;

        switch (button)
        {
            case MessageBoxButton.YesNo:
                YesButton.Visibility = Visibility.Visible;
                NoButton.Visibility = Visibility.Visible;
                break;
            case MessageBoxButton.YesNoCancel:
                YesButton.Visibility = Visibility.Visible;
                NoButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;
                break;
            case MessageBoxButton.OKCancel:
                OkButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Visible;
                break;
            default:
                OkButton.Visibility = Visibility.Visible;
                break;
        }
    }

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.Yes;
        Close();
    }

    private void No_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.No;
        Close();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.OK;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = MessageBoxResult.Cancel;
        Close();
    }
}
