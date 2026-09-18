using System;
using System.Windows.Forms;
using TodoApp.Models;

namespace TodoApp.Services;

// The one member of TrayIconService that ReminderScheduler actually needs - letting it depend on
// this instead of the concrete class means tests can supply a fake instead of standing up a real
// WinForms NotifyIcon (which needs a UI thread/message loop, not available under xunit).
public interface ITrayNotifier
{
    void ShowReminderToast(string title, string message, TaskItem? singleTask);
}

public record TrayMenuInfo(
    int TotalOpenCount,
    int DueTodayCount,
    int OverdueCount,
    bool IsGoogleDriveConnected,
    string? GoogleDriveStatus
);

// Owns a single persistent tray icon for the app's lifetime, used both for the New Task /
// Show / Exit menu and for reminder notifications - one icon, rather than a second ephemeral
// one popping in and out alongside it every time a reminder fires.
public class TrayIconService : IDisposable, ITrayNotifier
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;

    public event Action? NewTaskRequested;
    public event Action? ShowRequested;
    public event Action? ShowTodayRequested;
    public event Action? SyncGoogleDriveRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    // Fired when a reminder toast's "Mark Complete" or "Snooze" button is clicked. These arrive
    // on whatever thread the toast platform invokes activation on, not the UI thread - callers
    // must marshal back via Dispatcher, same as the three events above.
    public event Action<Guid>? TaskCompleteRequested;
    public event Action<Guid, TimeSpan>? TaskSnoozeRequested;

    public Func<TrayMenuInfo>? MenuInfoProvider;

    public void RaiseShowRequested() => ShowRequested?.Invoke();

    public TrayIconService()
    {
        _menu = new ContextMenuStrip();
        _menu.Opening += OnContextMenuOpening;
        RebuildMenu();

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "Tasky",
            ContextMenuStrip = _menu
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowRequested?.Invoke();
        };

        ToastNotificationService.Initialize();
        ToastNotificationService.Activated += OnToastActivated;
    }

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();

        // 1. Primary actions
        var showItem = new ToolStripMenuItem("Open Tasky", null, (_, _) => ShowRequested?.Invoke())
        {
            Font = new System.Drawing.Font(_menu.Font, System.Drawing.FontStyle.Bold)
        };
        _menu.Items.Add(showItem);

        var quickAddItem = new ToolStripMenuItem("Quick Add Task... (Ctrl+Alt+T)", null, (_, _) => NewTaskRequested?.Invoke());
        _menu.Items.Add(quickAddItem);

        _menu.Items.Add(new ToolStripSeparator());

        // 2. Glanceable Work Info
        var info = MenuInfoProvider?.Invoke();
        if (info != null)
        {
            string todayText = info.OverdueCount > 0
                ? $"Today ({info.DueTodayCount} due, {info.OverdueCount} overdue)"
                : info.DueTodayCount > 0
                    ? $"Today ({info.DueTodayCount} due)"
                    : "Today (No tasks due)";

            var todayItem = new ToolStripMenuItem(todayText, null, (_, _) => ShowTodayRequested?.Invoke());
            _menu.Items.Add(todayItem);

            if (info.IsGoogleDriveConnected)
            {
                string syncText = string.IsNullOrWhiteSpace(info.GoogleDriveStatus)
                    ? "Sync Google Drive Now"
                    : $"Sync Google Drive Now ({info.GoogleDriveStatus})";

                var syncItem = new ToolStripMenuItem(syncText, null, (_, _) => SyncGoogleDriveRequested?.Invoke());
                _menu.Items.Add(syncItem);
            }

            _menu.Items.Add(new ToolStripSeparator());
        }

        // 3. Settings & Exit
        _menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => SettingsRequested?.Invoke()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit Tasky", null, (_, _) => ExitRequested?.Invoke()));
    }

    public void UpdateTrayTooltip(int openCount, int todayCount, int overdueCount)
    {
        string text;
        if (overdueCount > 0)
            text = $"Tasky - {overdueCount} overdue, {todayCount} today";
        else if (todayCount > 0)
            text = $"Tasky - {todayCount} due today";
        else if (openCount > 0)
            text = $"Tasky - {openCount} open tasks";
        else
            text = "Tasky - All caught up!";

        if (text.Length > 63) text = text[..63];
        try { _icon.Text = text; } catch { /* Best-effort tooltip */ }
    }

    public void ShowCloseToTrayBalloon()
    {
        ShowBalloon("Tasky is running in the background", "Click or right-click this icon to open Tasky or add tasks.");
    }

    // singleTask is null for a batch "N tasks are due" reminder - see ShowReminderToast's own
    // doc comment for why the action buttons only make sense for a single task.
    public void ShowReminderToast(string title, string message, TaskItem? singleTask)
    {
        try
        {
            ToastNotificationService.ShowReminderToast(title, message, singleTask);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("TrayIconService", $"Toast notification failed, falling back to a balloon tip: {ex.Message}");
            ShowBalloon(title, message);
        }
    }

    private void OnToastActivated(ReminderToastActivated e)
    {
        switch (e.Action)
        {
            case ReminderToastAction.View:
                ShowRequested?.Invoke();
                break;
            case ReminderToastAction.MarkComplete when e.TaskId is { } id:
                TaskCompleteRequested?.Invoke(id);
                break;
            case ReminderToastAction.Snooze15 when e.TaskId is { } id:
                TaskSnoozeRequested?.Invoke(id, TimeSpan.FromMinutes(15));
                break;
            case ReminderToastAction.Snooze60 when e.TaskId is { } id:
                TaskSnoozeRequested?.Invoke(id, TimeSpan.FromHours(1));
                break;
        }
    }

    // Fallback only, used when the toast platform itself throws (e.g. Explorer/notification
    // service in a bad state) - notifications are best-effort and must never take down a
    // reminder check over it.
    private void ShowBalloon(string title, string message)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(8000);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("TrayIconService", $"Balloon tip fallback also failed: {ex.Message}");
        }
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var streamInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico"));
            return streamInfo is not null
                ? new System.Drawing.Icon(streamInfo.Stream)
                : System.Drawing.SystemIcons.Application;
        }
        catch
        {
            return System.Drawing.SystemIcons.Application;
        }
    }

    public void Dispose()
    {
        ToastNotificationService.Activated -= OnToastActivated;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
