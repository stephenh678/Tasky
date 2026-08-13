using System;
using System.Windows.Forms;

namespace TodoApp.Services;

// Owns a single persistent tray icon for the app's lifetime, used both for the New Task /
// Show / Exit menu and for reminder balloon tips - one icon, rather than a second ephemeral
// one popping in and out alongside it every time a reminder fires.
public class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;

    public event Action? NewTaskRequested;
    public event Action? ShowRequested;
    public event Action? ExitRequested;

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
    }

    public void ShowBalloon(string title, string message)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(8000);
        }
        catch (Exception)
        {
            // Notifications are best-effort; never let a failure here affect the rest of the app.
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
        _icon.Visible = false;
        _icon.Dispose();
    }
}
