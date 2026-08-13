using System;
using System.Windows;
using System.Windows.Input;
using TodoApp.Services;

namespace TodoApp;

public partial class QuickAddWindow : Window
{
    private bool _resultSet;

    public string? TaskTitle { get; private set; }

    public QuickAddWindow()
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        Loaded += (_, _) => TitleBox.Focus();
    }

    private void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TaskTitle = TitleBox.Text.Trim();
            SetResult(true);
        }
        else if (e.Key == Key.Escape)
        {
            SetResult(false);
        }
    }

    private void Window_Deactivated(object sender, EventArgs e) => SetResult(false);

    private void SetResult(bool result)
    {
        if (_resultSet) return;
        _resultSet = true;
        DialogResult = result;
    }
}
