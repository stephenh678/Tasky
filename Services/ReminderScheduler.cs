using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Threading;
using TodoApp.Models;

namespace TodoApp.Services;

/// <summary>
/// One "already reminded about this" record: the task, and the due date it was reminded FOR.
/// Remembering only the task ID (the original shape) meant a task that had fired once could never
/// fire again - reschedule an overdue task to next week and next week came and went silently,
/// because its ID was still in the set. Persisted as "guid|ticks"; a bare "guid" is an entry
/// written before the due date was recorded (DueDate == null) - see ReminderScheduler.IsNotified.
/// </summary>
public readonly record struct NotifiedReminder(Guid TaskId, DateTime? DueDate)
{
    public override string ToString()
        => DueDate is { } due ? $"{TaskId}|{due.Ticks.ToString(CultureInfo.InvariantCulture)}" : TaskId.ToString();

    public static NotifiedReminder? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('|');
        if (!Guid.TryParse(parts[0], out var id)) return null;
        if (parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
            && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks)
            return new NotifiedReminder(id, new DateTime(ticks));
        return new NotifiedReminder(id, null);
    }
}

/// <summary>
/// Owns due-task reminder polling: the 15-minute timer, a one-shot timer aimed at the next timed
/// due date, which reminders have already been shown, and snooze one-shot timers. Extracted out of
/// MainViewModel (see review_tasks.md's "Break up the MainViewModel god object" item) so the
/// due-task computation itself is unit-testable without constructing a full ViewModel.
/// </summary>
public class ReminderScheduler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    private readonly Func<IEnumerable<TaskItem>> _getTasks;
    private readonly Func<bool> _remindersEnabled;
    private readonly ITrayNotifier _tray;
    private readonly Action<IEnumerable<NotifiedReminder>>? _persistNotified;
    private readonly DispatcherTimer _reminderTimer;
    private readonly DispatcherTimer _nextDueTimer;
    private readonly Dictionary<Guid, DateTime?> _notified = new();
    private readonly Dictionary<Guid, DateTime> _snoozedUntil = new();
    // Legacy (ID-only) entries can't say which due date they were for. Anything due by the time
    // this session started is assumed to be what they covered; a due date later than that must
    // have been set since, so it notifies.
    private readonly DateTime _legacyEntryCutoff = DateTime.Now;
    private bool _reminderCheckInProgress;

    // Test-only introspection of which IDs are currently considered notified.
    public IReadOnlyCollection<Guid> NotifiedTaskIds => _notified.Keys;

    // persistNotified is called (with the full current set) whenever it changes, so a restart
    // doesn't re-notify everything already due - see ClearNotified for the file-switch case.
    public ReminderScheduler(Func<IEnumerable<TaskItem>> getTasks, Func<bool> remindersEnabled, ITrayNotifier tray,
        IEnumerable<NotifiedReminder>? initialNotified = null, Action<IEnumerable<NotifiedReminder>>? persistNotified = null)
    {
        _getTasks = getTasks;
        _remindersEnabled = remindersEnabled;
        _tray = tray;
        _persistNotified = persistNotified;
        foreach (var entry in initialNotified ?? Enumerable.Empty<NotifiedReminder>())
            _notified[entry.TaskId] = entry.DueDate;

        _reminderTimer = new DispatcherTimer { Interval = PollInterval };
        _reminderTimer.Tick += (_, _) => CheckReminders();

        _nextDueTimer = new DispatcherTimer();
        _nextDueTimer.Tick += (_, _) =>
        {
            _nextDueTimer.Stop();
            CheckReminders();
        };
    }

    public void Start() => _reminderTimer.Start();

    // Called from MainViewModel.LoadFile - switching files means every previously-notified task
    // ID belongs to a file that's no longer open, so the next check should re-evaluate fresh.
    public void ClearNotified()
    {
        _notified.Clear();
        Persist();
    }

    private void Persist() => _persistNotified?.Invoke(_notified.Select(kv => new NotifiedReminder(kv.Key, kv.Value)));

    // Pure: which not-yet-notified tasks are due or overdue as of `now`. Split out from
    // CheckReminders so this decision is testable without a DispatcherTimer or TrayIconService.
    // A due date's time-of-day matters only when one was actually set: the WPF DatePicker always
    // writes midnight (a date-only pick), while QuickEntryParser always writes a real time (an
    // explicit "@3pm", or its own 9 AM default) - so midnight means "due sometime that day" (fire
    // from the first poll on/after that date) and anything else means "due at that instant."
    public static List<TaskItem> GetDueTasks(IEnumerable<TaskItem> tasks, DateTime now, ISet<Guid> alreadyNotified)
        => GetDueTasks(tasks, now, t => alreadyNotified.Contains(t.Id));

    public static List<TaskItem> GetDueTasks(IEnumerable<TaskItem> tasks, DateTime now, Func<TaskItem, bool> isAlreadyNotified)
        => tasks.Where(t => !t.IsDone && !t.IsClosed && t.DueDate.HasValue && IsDueAsOf(t.DueDate.Value, now)
                             && !isAlreadyNotified(t))
            .ToList();

    private static bool IsDueAsOf(DateTime dueDate, DateTime now)
        => dueDate.TimeOfDay == TimeSpan.Zero ? dueDate.Date <= now.Date : dueDate <= now;

    // "Already reminded" means reminded about THIS due date. A different one - the task was
    // rescheduled, here or on another device via sync - is a new reminder.
    internal bool IsNotified(TaskItem task)
    {
        if (_snoozedUntil.TryGetValue(task.Id, out var until) && until > DateTime.Now) return true;
        if (!_notified.TryGetValue(task.Id, out var notifiedFor)) return false;
        return notifiedFor is { } due ? due == task.DueDate : task.DueDate <= _legacyEntryCutoff;
    }

    public void CheckReminders()
    {
        // Prevent overlapping reminder checks if the previous check is still running
        if (_reminderCheckInProgress || !_remindersEnabled()) return;

        _reminderCheckInProgress = true;
        try
        {
            var tasks = _getTasks().ToList();
            var changed = PruneNotified(tasks);

            var due = GetDueTasks(tasks, DateTime.Now, IsNotified);
            foreach (var t in due) _notified[t.Id] = t.DueDate;
            if (changed || due.Count > 0) Persist();

            if (due.Count == 1)
                _tray.ShowReminderToast("Task due", due[0].Text, due[0]);
            else if (due.Count > 1)
                _tray.ShowReminderToast("Tasks due", $"{due.Count} tasks are due or overdue.", null);

            ArmNextDueTimer(tasks);
        }
        finally
        {
            _reminderCheckInProgress = false;
        }
    }

    // Entries for tasks that no longer exist (deleted, or synced away) were never removed, so
    // settings.json's NotifiedTaskIds only ever grew. Completed/trashed tasks are dropped too:
    // they can't notify while in that state, and one that's reopened later should be able to.
    private bool PruneNotified(List<TaskItem> tasks)
    {
        var live = tasks.Where(t => !t.IsDone && !t.IsClosed).Select(t => t.Id).ToHashSet();
        var stale = _notified.Keys.Where(id => !live.Contains(id)).ToList();
        foreach (var id in stale) _notified.Remove(id);
        return stale.Count > 0;
    }

    /// <summary>
    /// Re-aims the one-shot timer at the next timed due date. MainViewModel calls this whenever
    /// tasks change, so a task created as "in 5 minutes" is picked up without waiting for a poll.
    /// </summary>
    public void Reschedule()
    {
        if (!_remindersEnabled())
        {
            _nextDueTimer.Stop();
            return;
        }
        ArmNextDueTimer(_getTasks());
    }

    // The 15-minute poll alone meant a task due "@3pm" could be announced as late as 3:15. Only
    // timed due dates need this (date-only ones fire from the first poll of the day), and only
    // ones inside the next poll window - anything further out will be seen by a later poll, which
    // re-arms this with fresher data anyway.
    private void ArmNextDueTimer(IEnumerable<TaskItem> tasks)
    {
        _nextDueTimer.Stop();
        var now = DateTime.Now;
        if (NextTimedDue(tasks, now, IsNotified) is not { } next) return;

        var delay = next - now;
        if (delay > PollInterval) return;
        // A hair past the due instant, so IsDueAsOf's `dueDate <= now` is true when it fires.
        _nextDueTimer.Interval = delay + TimeSpan.FromMilliseconds(250);
        _nextDueTimer.Start();
    }

    // Pure, for tests: the soonest future timed due date among tasks that could still notify.
    public static DateTime? NextTimedDue(IEnumerable<TaskItem> tasks, DateTime now, Func<TaskItem, bool> isAlreadyNotified)
    {
        DateTime? next = null;
        foreach (var t in tasks)
        {
            if (t.IsDone || t.IsClosed || t.DueDate is not { } due) continue;
            if (due.TimeOfDay == TimeSpan.Zero || due <= now || isAlreadyNotified(t)) continue;
            if (next is null || due < next) next = due;
        }
        return next;
    }

    // Entry point for the reminder toast's "Snooze 15m"/"Snooze 1 Hour" buttons. Rather than
    // changing the task's DueDate (which would be a real, saved edit the user didn't ask for),
    // this just clears the "already notified" flag so CheckReminders will pick the task back up,
    // and arms a one-shot timer so that happens close to the requested delay rather than waiting
    // for the next 15-minute polling tick.
    public void SnoozeTaskById(Guid taskId, TimeSpan duration)
    {
        if (_getTasks().All(t => t.Id != taskId)) return;

        // Dropped from the persisted set right away (so quitting mid-snooze re-announces it on the
        // next launch instead of losing the reminder for good), but held back in memory until the
        // snooze ends: clearing the flag alone meant any 15-minute poll landing inside a 1-hour
        // snooze re-announced the task the user had just asked to be left alone about.
        _snoozedUntil[taskId] = DateTime.Now + duration;
        if (_notified.Remove(taskId)) Persist();

        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _snoozedUntil.Remove(taskId);
            CheckReminders();
        };
        timer.Start();
    }
}
