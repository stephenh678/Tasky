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
