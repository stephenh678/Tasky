using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using TodoApp.Services;

namespace TodoApp;

public enum GoogleDriveFilePickerResult { Cancelled, UseExisting, CreateNew }

public partial class SelectGoogleDriveFileWindow : Window
{
    private class RemoteFileEntry
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string ModifiedText { get; init; } = "";
    }

    public GoogleDriveFilePickerResult Result { get; private set; } = GoogleDriveFilePickerResult.Cancelled;
    public string? SelectedRemoteFileId { get; private set; }
    public string? SelectedRemoteFileName { get; private set; }

    public SelectGoogleDriveFileWindow(IEnumerable<Google.Apis.Drive.v3.Data.File> remoteFiles)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);

        RemoteFileListBox.ItemsSource = remoteFiles
            .OrderByDescending(f => f.ModifiedTimeDateTimeOffset)
            .Select(f => new RemoteFileEntry
            {
                Id = f.Id,
                Name = f.Name ?? "(unnamed)",
                ModifiedText = f.ModifiedTimeDateTimeOffset is { } modified
                    ? $"Modified {modified.LocalDateTime:MMM d, yyyy 'at' h:mm tt}"
                    : "Modified date unknown"
            })
            .ToList();
    }

    private void RemoteFileListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UseSelectedButton.IsEnabled = RemoteFileListBox.SelectedItem is not null;

    private void UseSelected_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteFileListBox.SelectedItem is not RemoteFileEntry entry) return;
        SelectedRemoteFileId = entry.Id;
        SelectedRemoteFileName = entry.Name;
        Result = GoogleDriveFilePickerResult.UseExisting;
        DialogResult = true;
    }

    private void CreateNew_Click(object sender, RoutedEventArgs e)
    {
        Result = GoogleDriveFilePickerResult.CreateNew;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = GoogleDriveFilePickerResult.Cancelled;
        DialogResult = false;
    }
}
