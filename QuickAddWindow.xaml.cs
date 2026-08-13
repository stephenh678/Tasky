using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using TodoApp.Behaviors;
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

    // Losing focus (alt-tab, a notification stealing activation, clicking another monitor) used
    // to always discard the window - fine when it's still empty, but silently throwing away
    // something the user actually typed (with zero recovery) was the actual bug. An empty box
    // still auto-dismisses so the always-on-top capture window doesn't linger unnecessarily.
    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text)) SetResult(false);
    }

    private void TitleBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb) SpellCheckContextMenu.RepositionCaret(tb, e);
    }

    private void TitleBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is TextBox tb) SpellCheckContextMenu.MergeSuggestions(tb);
    }

    private void SetResult(bool result)
    {
        if (_resultSet) return;
        _resultSet = true;

        // Deactivated can fire in reentrant timing - e.g. right-clicking the tray icon while this
        // window is open steals activation via a separate native (WinForms) message pump, layered
        // on top of this window's own ShowDialog() frame. If that happens while the window is
        // already in the middle of closing via another path, setting DialogResult throws
        // InvalidOperationException ("...while a Window is closing"). There's nothing meaningful
        // to do about it - the window is already on its way down - and calling Close() here as a
        // fallback throws the exact same exception for the exact same reason, so just swallow it.
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
        }
    }
}
