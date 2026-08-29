using System;
using System.Collections.Generic;
using System.Linq;
using TodoApp.Models;
using TodoApp.Services;

namespace TodoApp.Tests;

// Was previously unreachable by any test: MainViewModel.CheckReminders had no seam for a test to
// construct without also standing up a full ViewModel (settings, theme, TrayIconService, DispatcherTimer).
// Extracting the pure "which tasks are due" decision into ReminderScheduler.GetDueTasks (see
// review_tasks.md's "Break up the MainViewModel god object" item) makes this directly testable.
public class ReminderSchedulerTests
{
    private static TaskItem Task(DateTime? dueDate, bool isDone = false, bool isClosed = false) =>
        new() { Text = "t", DueDate = dueDate, IsDone = isDone, IsClosed = isClosed };

    [Fact]
    public void OverdueTask_IsDue()
    {
        var today = new DateTime(2026, 8, 25);
        var task = Task(today.AddDays(-1));

        var due = ReminderScheduler.GetDueTasks(new[] { task }, today, new HashSet<Guid>());

        Assert.Contains(task, due);
    }

    [Fact]
    public void FutureTask_IsNotDue()
    {
        var today = new DateTime(2026, 8, 25);
        var task = Task(today.AddDays(1));

        var due = ReminderScheduler.GetDueTasks(new[] { task }, today, new HashSet<Guid>());

        Assert.DoesNotContain(task, due);
    }

    [Fact]
    public void CompletedTask_IsNotDue()
    {
        var today = new DateTime(2026, 8, 25);
        var task = Task(today, isDone: true);

        var due = ReminderScheduler.GetDueTasks(new[] { task }, today, new HashSet<Guid>());

        Assert.Empty(due);
    }

    [Fact]
    public void TrashedTask_IsNotDue()
    {
        var today = new DateTime(2026, 8, 25);
        var task = Task(today, isClosed: true);

        var due = ReminderScheduler.GetDueTasks(new[] { task }, today, new HashSet<Guid>());

        Assert.Empty(due);
    }

    [Fact]
    public void TaskWithNoDueDate_IsNotDue()
    {
        var today = new DateTime(2026, 8, 25);
        var task = Task(null);

        var due = ReminderScheduler.GetDueTasks(new[] { task }, today, new HashSet<Guid>());

        Assert.Empty(due);
    }

    [Fact]
    public void AlreadyNotifiedTask_IsNotDueAgain()
    {
        var today = new DateTime(2026, 8, 25);
        var task = Task(today);

        var due = ReminderScheduler.GetDueTasks(new[] { task }, today, new HashSet<Guid> { task.Id });

        Assert.Empty(due);
    }

    // Date-only due dates (WPF DatePicker always writes midnight) still fire from the first poll
    // of the day, same as before this behavior was added.
    [Fact]
    public void DateOnlyDueDate_IsDueFromStartOfDay()
    {
        var dueDateMidnight = new DateTime(2026, 8, 25, 0, 0, 0);
        var now = new DateTime(2026, 8, 25, 6, 0, 0); // first poll after midnight
        var task = Task(dueDateMidnight);

        var due = ReminderScheduler.GetDueTasks(new[] { task }, now, new HashSet<Guid>());

        Assert.Contains(task, due);
    }

    // A due date with a real time component (quick-add's explicit @3pm, or its 9 AM default)
    // must not fire before that time even though the date itself has arrived.
    [Fact]
    public void TimedDueDate_IsNotDueBeforeItsTime()
    {
        var dueAt3pm = new DateTime(2026, 8, 25, 15, 0, 0);
        var now = new DateTime(2026, 8, 25, 9, 0, 0);
        var task = Task(dueAt3pm);

        var due = ReminderScheduler.GetDueTasks(new[] { task }, now, new HashSet<Guid>());

        Assert.Empty(due);
    }

    [Fact]
    public void TimedDueDate_IsDueOnceItsTimeArrives()
    {
        var dueAt3pm = new DateTime(2026, 8, 25, 15, 0, 0);
        var now = new DateTime(2026, 8, 25, 15, 1, 0);
        var task = Task(dueAt3pm);

        var due = ReminderScheduler.GetDueTasks(new[] { task }, now, new HashSet<Guid>());

        Assert.Contains(task, due);
    }

    // Fake instead of the real TrayIconService: that class owns a live WinForms NotifyIcon and
    // needs a UI thread, neither of which is available under xunit. ITrayNotifier is the one
    // member ReminderScheduler actually calls, so this is enough to exercise it in isolation.
    private class FakeTrayNotifier : ITrayNotifier
    {
        public int CallCount { get; private set; }
        public void ShowReminderToast(string title, string message, TaskItem? singleTask) => CallCount++;
    }

    [Fact]
    public void Constructor_SeedsNotifiedIdsFromPersistedState()
    {
        var task = Task(new DateTime(2026, 8, 20)); // well overdue
        var tray = new FakeTrayNotifier();
        var scheduler = new ReminderScheduler(() => new[] { task }, () => true, tray,
            initialNotifiedIds: new[] { task.Id });

        // Would have notified (and bumped CallCount) if the persisted "already notified" set
        // hadn't been seeded in via the constructor.
        scheduler.CheckReminders();

        Assert.Equal(0, tray.CallCount);
        Assert.Contains(task.Id, scheduler.NotifiedTaskIds);
    }

    [Fact]
    public void CheckReminders_PersistsNewlyNotifiedIds()
    {
        var task = Task(new DateTime(2026, 8, 20));
        List<Guid>? persisted = null;
        var scheduler = new ReminderScheduler(() => new[] { task }, () => true, new FakeTrayNotifier(),
            persistNotified: ids => persisted = ids.ToList());

        scheduler.CheckReminders();

        Assert.NotNull(persisted);
        Assert.Contains(task.Id, persisted);
    }

    [Fact]
    public void ClearNotified_PersistsEmptySet()
    {
        var task = Task(new DateTime(2026, 8, 20));
        List<Guid>? persisted = null;
        var scheduler = new ReminderScheduler(() => new[] { task }, () => true, new FakeTrayNotifier(),
            initialNotifiedIds: new[] { task.Id }, persistNotified: ids => persisted = ids.ToList());

        scheduler.ClearNotified();

        Assert.NotNull(persisted);
        Assert.Empty(persisted);
    }
}
