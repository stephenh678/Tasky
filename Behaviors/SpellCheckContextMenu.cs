using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace TodoApp.Behaviors;

// Shared by every plain TextBox and RichTextBox field in the app (task title, note body, link
// label, checklist item text, tag entry, Quick Add's title box). WPF's built-in spelling-
// suggestions context menu only appears when a control has no ContextMenu of its own, so any
// field that also wants a custom right-click menu (Cut/Copy/Paste, or the RichTextBox formatting
// menu) has to merge the suggestions in by hand - this used to be copy-pasted at every call site.
internal static class SpellCheckContextMenu
{
    public static void RepositionCaret(TextBox tb, MouseButtonEventArgs e)
    {
        var index = tb.GetCharacterIndexFromPoint(e.GetPosition(tb), true);
        if (index >= 0) tb.CaretIndex = index;
    }

    public static void RepositionCaret(RichTextBox rtb, MouseButtonEventArgs e)
    {
        var position = rtb.GetPositionFromPoint(e.GetPosition(rtb), true);
        if (position is not null) rtb.CaretPosition = position;
    }

    public static void MergeSuggestions(TextBox tb)
    {
        if (tb.ContextMenu is not { } menu) return;
        Merge(menu, tb.GetSpellingError(tb.CaretIndex), tb);
    }

    public static void MergeSuggestions(RichTextBox rtb)
    {
        if (rtb.ContextMenu is not { } menu) return;
        Merge(menu, rtb.GetSpellingError(rtb.CaretPosition), rtb);
    }

    private static void Merge(ContextMenu menu, SpellingError? error, IInputElement commandTarget)
    {
        for (var i = menu.Items.Count - 1; i >= 0; i--)
        {
            if (menu.Items[i] is FrameworkElement { Tag: "SpellSuggestion" })
                menu.Items.RemoveAt(i);
        }

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
                CommandTarget = commandTarget
            });
        }

        if (index == 0)
            menu.Items.Insert(index++, new MenuItem { Header = "No spelling suggestions", IsEnabled = false, Tag = "SpellSuggestion" });

        menu.Items.Insert(index, new Separator { Tag = "SpellSuggestion" });
    }
}
