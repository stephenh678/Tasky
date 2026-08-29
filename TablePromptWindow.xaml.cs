using System.Windows;
using System.Windows.Input;
using TodoApp.Services;

namespace TodoApp;

public partial class TablePromptWindow : Window
{
    public int Columns { get; private set; } = 3;
    public int Rows { get; private set; } = 3;

    public TablePromptWindow()
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        Loaded += (_, _) =>
        {
            ColumnsBox.Focus();
            ColumnsBox.SelectAll();
        };
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(ColumnsBox.Text.Trim(), out var cols) && cols > 0 && cols <= 20)
            Columns = cols;
        else
            Columns = 3;

        if (int.TryParse(RowsBox.Text.Trim(), out var rows) && rows > 0 && rows <= 100)
            Rows = rows;
        else
            Rows = 3;

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
        if (e.Key == Key.Enter)
            Insert_Click(sender, e);
        else if (e.Key == Key.Escape)
            Cancel_Click(sender, e);
    }
}
