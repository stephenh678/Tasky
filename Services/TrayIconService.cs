using System;
using System.Windows.Forms;
using TodoApp.Models;

namespace TodoApp.Services;

// Owns a single persistent tray icon for the app's lifetime, used both for the New Task /
// Show / Exit menu and for reminder notifications - one icon, rather than a second ephemeral
// one popping in and out alongside it every time a reminder fires.
public class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;

    public event Action? NewTaskRequested;
    public event Action? ShowRequested;
    public event Action? ExitRequested;

    // Fired when a reminder toast's "Mark Complete" or "Snooze" button is clicked. These arrive
    // on whatever thread the toast platform invokes activation on, not the UI thread - callers
    // must marshal back via Dispatcher, same as the three events above.
    public event Action<Guid>? TaskCompleteRequested;
    public event Action<Guid, TimeSpan>? TaskSnoozeRequested;

    public TrayIconService()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("New Task", null, (_, _) => NewTaskRequested?.Invoke());
        menu.Items.Add("Show Tasky", null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "Tasky",
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();

        ToastNotificationService.Initialize();
        ToastNotificationService.Activated += OnToastActivated;
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
        var streamInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico"));
        return streamInfo is not null
            ? new System.Drawing.Icon(streamInfo.Stream)
            : System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        ToastNotificationService.Activated -= OnToastActivated;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
