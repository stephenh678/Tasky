using System.Collections.Generic;
using System.Windows;
using TodoApp.Services;

namespace TodoApp;

public partial class RestoreBackupWindow : Window
{
    public BackupInfo? SelectedBackup { get; private set; }

    public RestoreBackupWindow(List<BackupInfo> backups)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        BackupListBox.ItemsSource = backups;
        // Backups arrive newest-first (see TodoStore.ListBackups) - defaulting to that one lets
        // most visits (restore the most recent snapshot) skip picking at all, while still landing
        // on the confirmation dialog MainViewModel shows before anything actually changes.
        if (backups.Count > 0) BackupListBox.SelectedIndex = 0;
        Loaded += (_, _) => BackupListBox.Focus();
    }

    private void BackupListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => RestoreButton.IsEnabled = BackupListBox.SelectedItem is not null;

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        SelectedBackup = BackupListBox.SelectedItem as BackupInfo;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
