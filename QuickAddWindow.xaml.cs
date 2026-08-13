using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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

    // Same spell-suggestion-merge approach as MainWindow's text fields.
    private void TitleBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var index = tb.GetCharacterIndexFromPoint(e.GetPosition(tb), true);
        if (index >= 0) tb.CaretIndex = index;
    }

    private void TitleBox_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not TextBox tb || tb.ContextMenu is not { } menu) return;

        for (var i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is FrameworkElement { Tag: "SpellSuggestion" })
                menu.Items.RemoveAt(i);
        }

        var error = tb.GetSpellingError(tb.CaretIndex);
        if (error is null) return;

        var index = 0;
        foreach (var suggestion in error.Suggestions)
        {
            menu.Items.Insert(index++, new MenuItem
            {
                Header = suggestion,
                FontWeight = FontWeights.Bold,
                Tag = "SpellSuggestion",
                Command = EditingCommands.CorrectSpellingError,
                CommandParameter = suggestion,
                CommandTarget = tb
            });
        }

        if (index == 0)
            menu.Items.Insert(index++, new MenuItem { Header = "No spelling suggestions", IsEnabled = false, Tag = "SpellSuggestion" });

        menu.Items.Insert(index, new Separator { Tag = "SpellSuggestion" });
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
