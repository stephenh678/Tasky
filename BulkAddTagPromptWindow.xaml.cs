using System.Windows;
using System.Windows.Input;
using TodoApp.Services;

namespace TodoApp;

public partial class BulkAddTagPromptWindow : Window
{
    public string? TagResult { get; private set; }

    private readonly List<string> _availableTags;

    public BulkAddTagPromptWindow(int taskCount, IEnumerable<string> availableTags)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        PromptText.Text = $"Add tag to {taskCount} selected task(s):";
        _availableTags = availableTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        SuggestionsList.ItemsSource = _availableTags;
        Loaded += (_, _) => TagBox.Focus();
    }

    private void TagBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var q = TagUtils.Sanitize(TagBox.Text);
        SuggestionsList.ItemsSource = q.Length == 0
            ? _availableTags
            : _availableTags.Where(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void TagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Add_Click(sender, e);
        else if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }

    private void Suggestion_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SuggestionsList.SelectedItem is not string tag) return;
        TagResult = tag;
        DialogResult = true;
        Close();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (TagUtils.Sanitize(TagBox.Text).Length == 0) return;
        TagResult = TagBox.Text;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
