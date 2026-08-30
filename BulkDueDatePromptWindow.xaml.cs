using System.Windows;
using System.Windows.Input;
using TodoApp.Services;

namespace TodoApp;

public partial class BulkDueDatePromptWindow : Window
{
    // null means "Clear Due Date" was chosen, not "the user cancelled" - Cancel/Escape never set
    // DialogResult to true, so the caller only ever reads this after a genuine Set or Clear.
    public DateTime? DueDateResult { get; private set; }

    public BulkDueDatePromptWindow(int taskCount)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        PromptText.Text = $"Due date for {taskCount} selected task(s):";
        Loaded += (_, _) => DueDatePicker.Focus();
    }

    private void Set_Click(object sender, RoutedEventArgs e)
    {
        if (DueDatePicker.SelectedDate is not DateTime date) return;
        DueDateResult = date;
        DialogResult = true;
        Close();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        DueDateResult = null;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !DueDatePicker.IsDropDownOpen)
        {
            Set_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            Cancel_Click(sender, e);
        }
    }
}
