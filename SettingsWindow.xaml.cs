using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TodoApp.Services;
using TodoApp.ViewModels;

namespace TodoApp;

public enum SettingsSection { General, Backup, GoogleDrive, Advanced }

// One consolidated home for every persisted preference, replacing what used to be scattered
// across File/View/Help menu checkboxes plus two separate dialogs (the old BackupSettingsWindow
// and GoogleDriveWindow). General/Backup/Advanced bind straight to MainViewModel properties (the
// same ones the old menu checkboxes bound to), so every change here applies live - there's
// deliberately no separate Save button, just Close.
public partial class SettingsWindow : Window
{
    private static readonly Regex DigitsOnly = new(@"^\d+$");
    private readonly MainViewModel _viewModel;

    public SettingsWindow(MainViewModel viewModel, GoogleDriveSettingsControl driveControl, SettingsSection initialSection)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        _viewModel = viewModel;
        DataContext = viewModel;
        GoogleDriveHost.Content = driveControl;

        // Section switching itself is handled declaratively in XAML now (each panel's Visibility
        // binds to NavList.SelectedItem.Tag via the same EnumEqualsVisibilityConverter already
        // used elsewhere in the app) - this only needs to pick the initial item.
        foreach (ListBoxItem item in NavList.Items)
        {
            if (item.Tag is string tag && tag == initialSection.ToString())
            {
                NavList.SelectedItem = item;
                break;
            }
        }
        if (NavList.SelectedItem is null) NavList.SelectedIndex = 0;

        // ComboBox.SelectedValue matching (SelectedValuePath="Tag" bound against an int property)
        // doesn't work here: WPF compares the source int against each item's Tag with a plain
        // object.Equals, and a boxed int is never equal to the boxed string a XAML Tag="1440"
        // actually is - the dropdown would show no selection even with a valid stored value.
        // Matching by hand here (same approach as the NavList loop above) sidesteps that entirely.
        foreach (ComboBoxItem item in IntervalCombo.Items)
        {
            if (item.Tag is string tag && tag == viewModel.AutoBackupIntervalMinutes.ToString())
            {
                IntervalCombo.SelectedItem = item;
                break;
            }
        }
    }

    private void IntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalCombo.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var minutes))
            _viewModel.AutoBackupIntervalMinutes = minutes;
    }

    private void RetentionDaysTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !DigitsOnly.IsMatch(e.Text);

    // PreviewTextInput above only covers typed keystrokes - it never fires for a paste (Ctrl+V or
    // the context-menu command), so non-numeric pasted text would otherwise reach the Text binding
    // unfiltered and silently fail the int conversion. DataObject.Pasting is the separate hook WPF
    // provides specifically because PreviewTextInput doesn't cover this case.
    private void RetentionDaysTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text) || !DigitsOnly.IsMatch((string)e.DataObject.GetData(DataFormats.Text)))
            e.CancelCommand();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
