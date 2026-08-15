using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using TodoApp.Services;

namespace TodoApp;

public partial class GoogleDriveWindow : Window
{
    private readonly GoogleDriveService _driveService;
    private readonly Settings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly Func<Task> _onSyncRequested;

    public GoogleDriveWindow(GoogleDriveService driveService, Settings settings, SettingsStore settingsStore, Func<Task> onSyncRequested)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        _driveService = driveService;
        _settings = settings;
        _settingsStore = settingsStore;
        _onSyncRequested = onSyncRequested;

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
        }
        else
        {
            StatusText.Text = "Disconnected";
            AccountEmailText.Text = "No account connected";
            DisconnectButton.Visibility = Visibility.Collapsed;
            ConnectButton.Visibility = Visibility.Visible;
            ConnectButton.Content = "🔑 Connect & Sign In with Google";
            SyncNowButton.Visibility = Visibility.Collapsed;
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

                UpdateConnectionStatusUI();
                ProgressStatusText.Text = "Connected! Running initial sync to Google Drive...";

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
            AppLogger.Error("GoogleDriveWindow", "Connect error", ex);
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
            AppLogger.Error("GoogleDriveWindow", "SyncNow error", ex);
        }
        finally
        {
            SyncNowButton.IsEnabled = _driveService.IsAuthenticated;
        }
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
            AppLogger.Error("GoogleDriveWindow", "Failed to open Google Cloud Console link", ex);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
