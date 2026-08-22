using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TodoApp.Services;

namespace TodoApp;

// Was GoogleDriveWindow (a standalone dialog) - now embedded as one section of the unified
// SettingsWindow instead of its own separate window. Behavior is unchanged from that version;
// only Window-specific bits (title bar theming, the old Close button, Owner=this for the file
// picker) were adapted for living inside another window's content instead of being one itself.
public partial class GoogleDriveSettingsControl : UserControl
{
    private readonly GoogleDriveService _driveService;
    private readonly Settings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly Func<Task> _onSyncRequested;
    private readonly Func<string, string, Task<bool>> _onAttachExistingRemoteFile;
    private readonly Func<bool> _onCreateNewLocalFile;

    public GoogleDriveSettingsControl(
        GoogleDriveService driveService, Settings settings, SettingsStore settingsStore, Func<Task> onSyncRequested,
        Func<string, string, Task<bool>> onAttachExistingRemoteFile, Func<bool> onCreateNewLocalFile)
    {
        InitializeComponent();
        _driveService = driveService;
        _settings = settings;
        _settingsStore = settingsStore;
        _onSyncRequested = onSyncRequested;
        _onAttachExistingRemoteFile = onAttachExistingRemoteFile;
        _onCreateNewLocalFile = onCreateNewLocalFile;

        PopulateUI();
    }

    private void PopulateUI()
    {
        ClientIdTextBox.Text = _settings.GoogleDriveClientId ?? string.Empty;
        ClientSecretTextBox.Text = _settings.GoogleDriveClientSecret ?? string.Empty;

        UpdateConnectionStatusUI();
    }

    private void UpdateConnectionStatusUI()
    {
        if (_driveService.IsAuthenticated)
        {
            StatusText.Text = "Connected to Google Drive";
            AccountEmailText.Text = _settings.GoogleDriveAccountEmail ?? "Connected User";
            DisconnectButton.Visibility = Visibility.Visible;
            ConnectButton.Visibility = Visibility.Collapsed;
            SyncNowButton.Visibility = Visibility.Visible;
            SyncNowButton.IsEnabled = true;
            ChooseFileButton.Visibility = Visibility.Visible;
            ChooseFileButton.IsEnabled = true;
        }
        else
        {
            StatusText.Text = "Disconnected";
            AccountEmailText.Text = "No account connected";
            DisconnectButton.Visibility = Visibility.Collapsed;
            ConnectButton.Visibility = Visibility.Visible;
            ConnectButton.Content = "🔑 Connect & Sign In with Google";
            SyncNowButton.Visibility = Visibility.Collapsed;
            ChooseFileButton.Visibility = Visibility.Collapsed;
        }

        LastSyncText.Text = _settings.LastGoogleDriveSyncTime.HasValue
            ? $"Last Synced: {_settings.LastGoogleDriveSyncTime.Value:MMM d, yyyy 'at' h:mm:ss tt}"
            : "Last Synced: Never";
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var clientId = string.IsNullOrWhiteSpace(ClientIdTextBox.Text) ? null : ClientIdTextBox.Text.Trim();
        var clientSecret = string.IsNullOrWhiteSpace(ClientSecretTextBox.Text) ? null : ClientSecretTextBox.Text.Trim();

        ProgressStatusText.Text = "Opening browser for Google authentication...";
        ConnectButton.IsEnabled = false;

        try
        {
            var success = await _driveService.AuthenticateAsync(clientId, clientSecret);
            if (success)
            {
                var email = await _driveService.GetAccountEmailAsync();

                _settings.GoogleDriveClientId = clientId;
                _settings.GoogleDriveClientSecret = clientSecret;
                _settings.IsGoogleDriveEnabled = true;
                _settings.GoogleDriveAccountEmail = email;
                _settingsStore.Save(_settings);

                // This control is constructed fresh every time Settings is opened, never cached -
                // if the user closes the Settings window while the interactive browser auth above
                // was still in flight, this control is no longer hosted in any visible window by
                // the time we get here. IsLoaded goes false once a control's window closes. The
                // settings just persisted above are real and should stick regardless; what this
                // guards is only the UI updates and the file-picker/sync flow below, which need a
                // live window and would otherwise run silently against a detached control.
                if (!IsLoaded) return;

                UpdateConnectionStatusUI();

                // First-ever connect on this device is exactly the moment ambiguity matters most:
                // rather than silently syncing whatever file happens to already be open locally,
                // ask whether that's actually what the user wants when files already exist on
                // Drive (a duplicate-named default file would otherwise clobber real data - see
                // the file-overwrite fix this same cache change addresses).
                if (!await OfferFilePickerAsync(silentIfNoneFound: true))
                {
                    ProgressStatusText.Text = "Connected. Choose a file and sync when you're ready.";
                    return;
                }

                ProgressStatusText.Text = "Running sync to Google Drive...";

                try
                {
                    await _onSyncRequested();
                    UpdateConnectionStatusUI();
                    ProgressStatusText.Text = "Successfully connected & synced to Google Drive!";
                }
                catch (Exception syncEx)
                {
                    ProgressStatusText.Text = $"Connected, but initial sync had an issue: {syncEx.Message}";
                }
            }
            else
            {
                ProgressStatusText.Text = "Authentication failed or was cancelled.";
            }
        }
        catch (Exception ex)
        {
            ProgressStatusText.Text = $"Authentication error: {ex.Message}";
            AppLogger.Error("GoogleDriveSettingsControl", "Connect error", ex);
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var confirm = ThemedMessageBox.Show("Disconnect Google Drive account from Tasky?\nYour local task data file will remain untouched.",
            "Disconnect Google Drive", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        await _driveService.SignOutAsync();

        _settings.IsGoogleDriveEnabled = false;
        _settings.GoogleDriveAccountEmail = null;
        _settings.GoogleDriveFileId = null;
        _settings.GoogleDriveFileIdsByFile.Clear();
        _settingsStore.Save(_settings);

        ProgressStatusText.Text = "Disconnected from Google Drive.";
        UpdateConnectionStatusUI();
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        if (!_driveService.IsAuthenticated) return;

        SyncNowButton.IsEnabled = false;
        ProgressStatusText.Text = "Syncing with Google Drive...";

        try
        {
            await _onSyncRequested();
            UpdateConnectionStatusUI();
            ProgressStatusText.Text = "Sync completed successfully!";
        }
        catch (Exception ex)
        {
            ProgressStatusText.Text = $"Sync failed: {ex.Message}";
            AppLogger.Error("GoogleDriveSettingsControl", "SyncNow error", ex);
        }
        finally
        {
            SyncNowButton.IsEnabled = _driveService.IsAuthenticated;
        }
    }

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        if (!_driveService.IsAuthenticated) return;

        ChooseFileButton.IsEnabled = false;
        try
        {
            if (!await OfferFilePickerAsync(silentIfNoneFound: false)) return;

            ProgressStatusText.Text = "Syncing with Google Drive...";
            await _onSyncRequested();
            UpdateConnectionStatusUI();
            ProgressStatusText.Text = "Sync completed successfully!";
        }
        catch (Exception ex)
        {
            ProgressStatusText.Text = $"Sync failed: {ex.Message}";
            AppLogger.Error("GoogleDriveSettingsControl", "ChooseFile error", ex);
        }
        finally
        {
            ChooseFileButton.IsEnabled = _driveService.IsAuthenticated;
        }
    }

    /// <summary>
    /// Shows the existing-remote-files picker and carries out whatever the user chose. Returns
    /// false when the caller should stop short of running a sync afterward - either the user
    /// explicitly backed out of the picker (closed it, or backed out of "Create New" without
    /// picking a destination), and a sync right afterward would defeat the point of asking.
    /// "Nothing to pick from" is the one case that still falls through to a sync - there was
    /// nothing to choose, so it's the same as the pre-picker default (and the same as hitting
    /// "Sync Now" directly).
    /// </summary>
    private async Task<bool> OfferFilePickerAsync(bool silentIfNoneFound)
    {
        var remoteFiles = await _driveService.ListTaskyFilesAsync();
        if (remoteFiles.Count == 0)
        {
            if (!silentIfNoneFound)
                ThemedMessageBox.Show("No Tasky files were found on Google Drive yet.", "Choose File",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }

        var picker = new SelectGoogleDriveFileWindow(remoteFiles) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true) return false;

        if (picker.Result == GoogleDriveFilePickerResult.UseExisting
            && picker.SelectedRemoteFileId is not null && picker.SelectedRemoteFileName is not null)
        {
            ProgressStatusText.Text = $"Attaching '{picker.SelectedRemoteFileName}'...";
            // Reflects whether attaching actually completed - false means the user backed out of
            // a destination-picker dialog partway through, in which case the caller shouldn't
            // run a sync right afterward (there was previously a bug here where it did so
            // unconditionally, causing a sync to fire even after backing out).
            return await _onAttachExistingRemoteFile(picker.SelectedRemoteFileId, picker.SelectedRemoteFileName);
        }

        if (picker.Result == GoogleDriveFilePickerResult.CreateNew)
            return _onCreateNewLocalFile();

        return true;
    }

    private void ImportJson_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import client_secret.json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "client_secret.json"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement clientElement;
            if (root.TryGetProperty("installed", out clientElement) || root.TryGetProperty("web", out clientElement))
            {
                var clientId = clientElement.GetProperty("client_id").GetString();
                var clientSecret = clientElement.GetProperty("client_secret").GetString();

                if (!string.IsNullOrEmpty(clientId)) ClientIdTextBox.Text = clientId;
                if (!string.IsNullOrEmpty(clientSecret)) ClientSecretTextBox.Text = clientSecret;

                ProgressStatusText.Text = "Successfully imported credentials from JSON!";
            }
            else
            {
                ThemedMessageBox.Show("Could not find 'installed' or 'web' property in the selected JSON file.", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"Failed to parse client_secret.json: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenConsole_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://console.cloud.google.com/apis/credentials",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("GoogleDriveSettingsControl", "Failed to open Google Cloud Console link", ex);
        }
    }
}
