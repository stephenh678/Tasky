using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using TodoApp.Models;

namespace TodoApp.Services;

/// <summary>
/// Owns due-task reminder polling: the 15-minute timer, which task IDs have already been
/// notified about this session, and snooze one-shot timers. Extracted out of MainViewModel (see
/// review_tasks.md's "Break up the MainViewModel god object" item) so the due-task computation
/// itself is unit-testable without constructing a full ViewModel.
/// </summary>
public class ReminderScheduler
{
    private readonly Func<IEnumerable<TaskItem>> _getTasks;
    private readonly Func<bool> _remindersEnabled;
    private readonly ITrayNotifier _tray;
    private readonly Action<IEnumerable<Guid>>? _persistNotified;
    private readonly DispatcherTimer _reminderTimer;
    private readonly HashSet<Guid> _notifiedTaskIds;
    private bool _reminderCheckInProgress;

    // Test-only introspection of which IDs are currently considered notified.
    public IReadOnlyCollection<Guid> NotifiedTaskIds => _notifiedTaskIds;

    // persistNotified is called (with the full current set) whenever it changes, so a restart
    // doesn't re-notify everything already due - see ClearNotified for the file-switch case.
    public ReminderScheduler(Func<IEnumerable<TaskItem>> getTasks, Func<bool> remindersEnabled, ITrayNotifier tray,
        IEnumerable<Guid>? initialNotifiedIds = null, Action<IEnumerable<Guid>>? persistNotified = null)
    {
        _getTasks = getTasks;
        _remindersEnabled = remindersEnabled;
        _tray = tray;
        _persistNotified = persistNotified;
        _notifiedTaskIds = initialNotifiedIds is null ? new HashSet<Guid>() : new HashSet<Guid>(initialNotifiedIds);

        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _reminderTimer.Tick += (_, _) => CheckReminders();
    }

    public void Start() => _reminderTimer.Start();

    // Called from MainViewModel.LoadFile - switching files means every previously-notified task
    // ID belongs to a file that's no longer open, so the next check should re-evaluate fresh.
    public void ClearNotified()
    {
        _notifiedTaskIds.Clear();
        _persistNotified?.Invoke(_notifiedTaskIds);
    }

    // Pure: which not-yet-notified tasks are due or overdue as of `now`. Split out from
    // CheckReminders so this decision is testable without a DispatcherTimer or TrayIconService.
    // A due date's time-of-day matters only when one was actually set: the WPF DatePicker always
    // writes midnight (a date-only pick), while QuickEntryParser always writes a real time (an
    // explicit "@3pm", or its own 9 AM default) - so midnight means "due sometime that day" (fire
    // from the first poll on/after that date) and anything else means "due at that instant."
    public static List<TaskItem> GetDueTasks(IEnumerable<TaskItem> tasks, DateTime now, ISet<Guid> alreadyNotified)
        => tasks.Where(t => !t.IsDone && !t.IsClosed && t.DueDate.HasValue && IsDueAsOf(t.DueDate.Value, now)
                             && !alreadyNotified.Contains(t.Id))
            .ToList();

    private static bool IsDueAsOf(DateTime dueDate, DateTime now)
        => dueDate.TimeOfDay == TimeSpan.Zero ? dueDate.Date <= now.Date : dueDate <= now;

    public void CheckReminders()
    {
        // Prevent overlapping reminder checks if the previous check is still running
        if (_reminderCheckInProgress || !_remindersEnabled()) return;

        _reminderCheckInProgress = true;
        try
        {
            var due = GetDueTasks(_getTasks(), DateTime.Now, _notifiedTaskIds);
            if (due.Count == 0) return;

            foreach (var t in due) _notifiedTaskIds.Add(t.Id);
            _persistNotified?.Invoke(_notifiedTaskIds);

            if (due.Count == 1)
                _tray.ShowReminderToast("Task due", due[0].Text, due[0]);
            else
                _tray.ShowReminderToast("Tasks due", $"{due.Count} tasks are due or overdue.", null);
        }
        finally
        {
            _reminderCheckInProgress = false;
        }
    }

    // Entry point for the reminder toast's "Snooze 15m"/"Snooze 1 Hour" buttons. Rather than
    // changing the task's DueDate (which would be a real, saved edit the user didn't ask for),
    // this just clears the "already notified" flag so CheckReminders will pick the task back up,
    // and arms a one-shot timer so that happens close to the requested delay rather than waiting
    // for the next 15-minute polling tick.
    public void SnoozeTaskById(Guid taskId, TimeSpan duration)
    {
        if (_getTasks().All(t => t.Id != taskId)) return;

        _notifiedTaskIds.Remove(taskId);
        _persistNotified?.Invoke(_notifiedTaskIds);

        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CheckReminders();
        };
        timer.Start();
    }
}
