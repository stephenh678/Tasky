using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using TodoApp.Services;

namespace TodoApp;

/// <summary>
/// Shown either right after a "Check for Updates" hit (announce -> download -> ready-to-restart)
/// or, if a previous session's download was left un-applied via "Later", straight at
/// ready-to-restart with no network call. Non-modal (Show, not ShowDialog) so a silent background
/// check never blocks whatever the user's doing - same reasoning as the toast notifications this
/// app already shows unprompted.
/// </summary>
public partial class UpdateAvailableWindow : Window
{
    // Only one of these should ever be open at a time - a background check completing while the
    // user already has a manually-triggered one open (or vice versa) should surface the same
    // window instead of stacking a second one.
    private static UpdateAvailableWindow? _open;

    private enum Phase { Announce, Downloading, ReadyToRestart }

    private readonly UpdateInfo _info;
    private Phase _phase;
    private CancellationTokenSource? _downloadCts;

    private UpdateAvailableWindow(UpdateInfo info, bool alreadyStaged)
    {
        InitializeComponent();
        ThemeService.ApplyTitleBar(this);
        _info = info;

        if (alreadyStaged) SetPhase(Phase.ReadyToRestart);
        else SetPhase(Phase.Announce);
    }

    /// <summary>Opens (or refocuses) the single shared instance for this update-available state.</summary>
    public static void ShowFor(Window owner, UpdateInfo info, bool alreadyStaged = false)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        _open = new UpdateAvailableWindow(info, alreadyStaged) { Owner = owner };
        _open.Closed += (_, _) => _open = null;
        _open.Show();
    }

    private void SetPhase(Phase phase)
    {
        _phase = phase;
        switch (phase)
        {
            case Phase.Announce:
                HeadlineText.Text = $"Tasky {_info.Version} is available";
                SubText.Text = $"You're on {UpdateService.CurrentVersion.ToString(3)}.";
                NotesBorder.Visibility = Visibility.Visible;
                NotesText.Text = string.IsNullOrWhiteSpace(_info.ReleaseNotes)
                    ? "No release notes were provided for this version."
                    : PlainTextFromMarkdown(_info.ReleaseNotes);
                PrimaryButton.Content = "Download & Install";
                PrimaryButton.IsEnabled = true;
                SecondaryButton.Visibility = Visibility.Visible;
                SecondaryButton.IsEnabled = true;
                LaterButton.Content = "Later";
                LaterButton.IsEnabled = true;
                break;

            case Phase.Downloading:
                HeadlineText.Text = $"Downloading Tasky {_info.Version}...";
                SubText.Text = "";
                PrimaryButton.IsEnabled = false;
                SecondaryButton.IsEnabled = false;
                LaterButton.Content = "Cancel";
                LaterButton.IsEnabled = true;
                break;

            case Phase.ReadyToRestart:
                HeadlineText.Text = $"Tasky {_info.Version} is ready to install";
                SubText.Text = "Tasky will close and reopen to finish updating - your work is saved automatically first.";
                NotesBorder.Visibility = Visibility.Collapsed;
                PrimaryButton.Content = "Relaunch Now";
                PrimaryButton.IsEnabled = true;
                SecondaryButton.Visibility = Visibility.Collapsed;
                LaterButton.Content = "Later";
                LaterButton.IsEnabled = true;
                break;
        }
    }

    private async void Primary_Click(object sender, RoutedEventArgs e)
    {
        switch (_phase)
        {
            case Phase.Announce:
                await DownloadAsync();
                break;
            case Phase.ReadyToRestart:
                RestartNow();
                break;
        }
    }

    private async System.Threading.Tasks.Task DownloadAsync()
    {
        SetPhase(Phase.Downloading);
        _downloadCts = new CancellationTokenSource();
        var progress = new Progress<double>(p => SubText.Text = $"{p:P0}");

        try
        {
            await UpdateService.StageUpdateAsync(_info, progress, _downloadCts.Token);
            SetPhase(Phase.ReadyToRestart);
        }
        catch (OperationCanceledException)
        {
            SetPhase(Phase.Announce);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UpdateAvailableWindow", $"Update download failed: {ex.Message}");
            ThemedMessageBox.Show(
                $"Couldn't download the update: {ex.Message}\n\nYou can grab it manually from the GitHub release page instead.",
                "Update Problem", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetPhase(Phase.Announce);
        }
        finally
        {
            _downloadCts = null;
        }
    }

    private void RestartNow()
    {
        try
        {
            UpdateService.ApplyUpdateAndRestart();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UpdateAvailableWindow", $"Couldn't launch the update helper: {ex.Message}");
            ThemedMessageBox.Show(
                $"Couldn't start the update: {ex.Message}",
                "Update Problem", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Close();
        Application.Current.MainWindow?.Close();
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_info.ReleaseUrl) || !_info.ReleaseUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            return;
        Process.Start(new ProcessStartInfo(_info.ReleaseUrl) { UseShellExecute = true });
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (_phase == Phase.Downloading)
        {
            _downloadCts?.Cancel();
            return;
        }
        Close();
    }

    // A plain TextBlock can't render Markdown - GitHub release bodies (this app's own release
    // notes, written by CONTRIBUTING.md's own commit-message convention) always use just a handful
    // of constructs (## headers, **bold**, - bullets, [text](url) links), so stripping those markers
    // gets readable plain text without pulling in a real Markdown-to-FlowDocument renderer for a
    // one-off dialog.
    private static string PlainTextFromMarkdown(string markdown)
    {
        var text = Regex.Replace(markdown, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"__(.+?)__", "$1");
        text = Regex.Replace(text, @"^[-*]\s+", "• ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\[(.+?)\]\([^)]+\)", "$1");
        return text.Trim();
    }
}
