using System;
using CommunityToolkit.WinUI.Notifications;
using TodoApp.Models;

namespace TodoApp.Services;

public enum ReminderToastAction
{
    View,
    MarkComplete,
    Snooze15,
    Snooze60,
}

public sealed record ReminderToastActivated(ReminderToastAction Action, Guid? TaskId);

/// <summary>
/// Native Windows 10/11 toast notifications for task reminders, replacing the old WinForms
/// balloon tip. Uses CommunityToolkit.WinUI.Notifications' compat layer, which works from an
/// unpackaged (non-MSIX) Win32 app like this one without needing a Start Menu shortcut or a
/// COM activator class - subscribing to OnActivated is enough to register everything it needs.
/// </summary>
public static class ToastNotificationService
{
    public static event Action<ReminderToastActivated>? Activated;

    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        ToastNotificationManagerCompat.OnActivated += OnActivated;
    }

    // singleTask is null for a "N tasks are due" batch reminder - there's no single task the
    // Mark Complete/Snooze buttons could unambiguously apply to, so those buttons are only added
    // when reminding about exactly one task.
    public static void ShowReminderToast(string title, string message, TaskItem? singleTask)
    {
        var builder = new ToastContentBuilder()
            .AddArgument("action", nameof(ReminderToastAction.View))
            .AddText(title)
            .AddText(message);

        if (singleTask is not null)
        {
            builder.AddArgument("taskId", singleTask.Id.ToString());

            builder.AddButton(new ToastButton()
                .SetContent("Mark Complete")
                .AddArgument("action", nameof(ReminderToastAction.MarkComplete))
                .SetBackgroundActivation());

            builder.AddButton(new ToastButton()
                .SetContent("Snooze 15m")
                .AddArgument("action", nameof(ReminderToastAction.Snooze15))
                .SetBackgroundActivation());

            builder.AddButton(new ToastButton()
                .SetContent("Snooze 1 Hour")
                .AddArgument("action", nameof(ReminderToastAction.Snooze60))
                .SetBackgroundActivation());
        }

        builder.Show();
    }

    // Non-MSIX apps register some registry-based COM/AUMID plumbing the first time OnActivated is
    // subscribed to (see Initialize) - Uninstall() reverses that. Called from a short-lived
    // "--cleanup-notifications" process launched by Uninstall-Tasky.ps1 right before it deletes
    // Tasky.exe, so nothing about toast registration is left behind after uninstall.
    public static void Uninstall() => ToastNotificationManagerCompat.Uninstall();

    private static void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);

        if (!args.Contains("action") || !Enum.TryParse<ReminderToastAction>(args["action"], out var action))
            return;

        Guid? taskId = args.Contains("taskId") && Guid.TryParse(args["taskId"], out var id) ? id : null;
        Activated?.Invoke(new ReminderToastActivated(action, taskId));
    }
}
