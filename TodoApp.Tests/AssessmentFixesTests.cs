using System.IO;
using TodoApp.Models;
using TodoApp.Services;
using TodoApp.ViewModels;

namespace TodoApp.Tests;

// Regression tests for the fixes that came out of the full-codebase assessment: symmetric sync
// conflicts, reschedule-aware reminders, month-day recurrence anchoring, atomic backup restore,
// updater checksum parsing, and MainViewModel's load/delete/recurrence paths.
public class SyncConflictSymmetryTests
{
    private static readonly DateTime LastSync = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static (TaskItem Local, TaskItem Remote) Pair(DateTime localModified, DateTime remoteModified)
    {
        var id = Guid.NewGuid();
        return (new TaskItem { Id = id, Text = "local edit", ModifiedAt = localModified },
                new TaskItem { Id = id, Text = "remote edit", ModifiedAt = remoteModified });
    }

    private static TaskMergePlan Plan(TaskItem local, TaskItem remote, DateTime? lastSync, DateTime? localBaseline = null)
        => TaskSyncMerge.ComputeMergePlan(new[] { local }, new[] { remote },
            Array.Empty<TaskSyncRecord>(), Array.Empty<TaskSyncRecord>(), lastSync, localBaseline);

    [Fact]
    public void LocalNewer_AndRemoteAlsoChangedSinceLastSync_KeepsRemoteEditAsConflictedCopy()
    {
        var (local, remote) = Pair(LastSync.AddHours(2), LastSync.AddHours(1));

        var plan = Plan(local, remote, LastSync);

        Assert.Empty(plan.TasksToUpdate); // local still wins the original ID
        var copy = Assert.Single(plan.ConflictedCopiesToAdd);
        Assert.Equal("remote edit (conflicted copy)", copy.Text);
        Assert.NotEqual(local.Id, copy.Id);
    }

    [Fact]
    public void LocalNewer_AndRemoteUnchangedSinceLastSync_IsAnOrdinaryUpload()
    {
        var (local, remote) = Pair(LastSync.AddHours(2), LastSync.AddHours(-1));

        var plan = Plan(local, remote, LastSync);

        Assert.Empty(plan.TasksToUpdate);
        Assert.Empty(plan.ConflictedCopiesToAdd);
    }

    [Fact]
    public void LocalNewer_NeverSyncedBefore_HasNoBaselineSoNoConflict()
    {
        var (local, remote) = Pair(LastSync.AddHours(2), LastSync.AddHours(1));

        Assert.Empty(Plan(local, remote, lastSync: null).ConflictedCopiesToAdd);
    }

    [Fact]
    public void EqualTimestamps_AreTheSameEdit()
    {
        var (local, remote) = Pair(LastSync.AddHours(1), LastSync.AddHours(1));

        var plan = Plan(local, remote, LastSync);

        Assert.Empty(plan.TasksToUpdate);
        Assert.Empty(plan.ConflictedCopiesToAdd);
    }

    // An edit made while the previous pass was still uploading: dated BEFORE that pass finished
    // (LastSync) but AFTER it captured what it uploaded (the local baseline), so it never reached
    // Drive. Judged against LastSync alone it looked "unchanged" and was overwritten silently.
    [Fact]
    public void RemoteNewer_LocalEditedDuringPreviousUpload_IsKeptAsConflictedCopy()
    {
        var uploadSnapshotTakenAt = LastSync.AddSeconds(-30);
        var (local, remote) = Pair(LastSync.AddSeconds(-10), LastSync.AddHours(1));

        var withoutBaseline = Plan(local, remote, LastSync);
        var withBaseline = Plan(local, remote, LastSync, uploadSnapshotTakenAt);

        Assert.Empty(withoutBaseline.ConflictedCopiesToAdd);
        Assert.Equal("local edit (conflicted copy)", Assert.Single(withBaseline.ConflictedCopiesToAdd).Text);
        Assert.Single(withBaseline.TasksToUpdate);
    }

    [Fact]
    public void ApplyTaskFields_CarriesRecurrenceAnchorDay()
    {
        var target = new TaskItem { RecurrenceAnchorDay = null };
        TaskSyncMerge.ApplyTaskFields(target, new TaskItem { RecurrenceAnchorDay = 31 });
        Assert.Equal(31, target.RecurrenceAnchorDay);
    }
}

public class ReminderRescheduleTests
{
    private class FakeTray : ITrayNotifier
    {
        public int CallCount { get; private set; }
        public void ShowReminderToast(string title, string message, TaskItem? singleTask) => CallCount++;
    }

    [Fact]
    public void ReschedulingAnAlreadyNotifiedTask_NotifiesAgainForTheNewDueDate()
    {
        var task = new TaskItem { Text = "t", DueDate = DateTime.Today.AddDays(-2) };
        var tray = new FakeTray();
        var scheduler = new ReminderScheduler(() => new[] { task }, () => true, tray);

        scheduler.CheckReminders();
        scheduler.CheckReminders();
        Assert.Equal(1, tray.CallCount); // same due date: once only

        task.DueDate = DateTime.Today.AddDays(-1); // rescheduled, and already due again
        scheduler.CheckReminders();
        Assert.Equal(2, tray.CallCount);
    }

    [Fact]
    public void LegacyIdOnlyEntry_StillSuppressesWhatWasAlreadyDue_ButNotALaterDueDate()
    {
        var task = new TaskItem { Text = "t", DueDate = DateTime.Today.AddDays(-2) };
        var tray = new FakeTray();
        var scheduler = new ReminderScheduler(() => new[] { task }, () => true, tray,
            initialNotified: new[] { new NotifiedReminder(task.Id, null) });

        scheduler.CheckReminders();
        Assert.Equal(0, tray.CallCount);

        task.DueDate = DateTime.Now.AddMinutes(30);
        Assert.False(scheduler.IsNotified(task));
    }

    [Fact]
    public void CheckReminders_PrunesEntriesForTasksThatAreGoneOrCompleted()
    {
        var open = new TaskItem { Text = "open", DueDate = DateTime.Today.AddDays(-1) };
        var done = new TaskItem { Text = "done", DueDate = DateTime.Today.AddDays(-1), IsDone = true };
        List<NotifiedReminder>? persisted = null;
        var scheduler = new ReminderScheduler(() => new[] { open, done }, () => true, new FakeTray(),
            initialNotified: new[]
            {
                new NotifiedReminder(open.Id, open.DueDate),
                new NotifiedReminder(done.Id, done.DueDate),
                new NotifiedReminder(Guid.NewGuid(), DateTime.Today), // task no longer exists
            },
            persistNotified: entries => persisted = entries.ToList());

        scheduler.CheckReminders();

        Assert.NotNull(persisted);
        Assert.Equal(open.Id, Assert.Single(persisted).TaskId);
    }

    [Fact]
    public void Snooze_SuppressesPollsUntilItEnds_ButDropsThePersistedFlag()
    {
        var task = new TaskItem { Text = "t", DueDate = DateTime.Today.AddDays(-1) };
        var tray = new FakeTray();
        List<NotifiedReminder>? persisted = null;
        var scheduler = new ReminderScheduler(() => new[] { task }, () => true, tray,
            persistNotified: entries => persisted = entries.ToList());
        scheduler.CheckReminders();

        scheduler.SnoozeTaskById(task.Id, TimeSpan.FromHours(1));
        scheduler.CheckReminders(); // a 15-minute poll landing inside the snooze

        Assert.Equal(1, tray.CallCount);
        Assert.Empty(persisted!); // quitting mid-snooze re-announces on next launch
    }

    [Fact]
    public void NextTimedDue_PicksTheSoonestFutureTimedTask_IgnoringDateOnlyDoneAndNotified()
    {
        var now = new DateTime(2026, 9, 18, 14, 0, 0);
        var soon = new TaskItem { DueDate = now.AddMinutes(5) };
        var later = new TaskItem { DueDate = now.AddMinutes(9) };
        var dateOnly = new TaskItem { DueDate = now.Date.AddDays(1) };
        var past = new TaskItem { DueDate = now.AddMinutes(-5) };
        var done = new TaskItem { DueDate = now.AddMinutes(1), IsDone = true };
        var notified = new TaskItem { DueDate = now.AddMinutes(2) };

        var next = ReminderScheduler.NextTimedDue(new[] { later, dateOnly, past, done, notified, soon }, now, t => t == notified);

        Assert.Equal(soon.DueDate, next);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NotifiedReminder_RoundTripsThroughItsStringForm(bool withDueDate)
    {
        var entry = new NotifiedReminder(Guid.NewGuid(), withDueDate ? new DateTime(2026, 9, 18, 15, 30, 0) : null);
        Assert.Equal(entry, NotifiedReminder.Parse(entry.ToString()));
    }

    [Fact]
    public void NotifiedReminder_Parse_RejectsGarbage()
    {
        Assert.Null(NotifiedReminder.Parse("not-a-guid"));
        Assert.Null(NotifiedReminder.Parse(""));
        Assert.Null(NotifiedReminder.Parse(null));
    }
}

public class RecurrenceAnchorDayTests
{
    [Fact]
    public void MonthlyOnThe31st_ReturnsToThe31stAfterAShortMonth()
    {
        var feb = MainViewModel.NextDueDate(new DateTime(2026, 1, 31, 17, 0, 0), RecurrenceRule.Monthly, 1, anchorDay: 31);
        var mar = MainViewModel.NextDueDate(feb, RecurrenceRule.Monthly, 1, anchorDay: 31);
        var apr = MainViewModel.NextDueDate(mar, RecurrenceRule.Monthly, 1, anchorDay: 31);

        Assert.Equal(new DateTime(2026, 2, 28, 17, 0, 0), feb);
        Assert.Equal(new DateTime(2026, 3, 31, 17, 0, 0), mar);
        Assert.Equal(new DateTime(2026, 4, 30, 17, 0, 0), apr);
    }

    [Fact]
    public void YearlyOnFeb29_ReturnsToThe29thInTheNextLeapYear()
    {
        var d2029 = MainViewModel.NextDueDate(new DateTime(2028, 2, 29), RecurrenceRule.Yearly, 1, anchorDay: 29);
        Assert.Equal(new DateTime(2029, 2, 28), d2029);
        Assert.Equal(new DateTime(2032, 2, 29), MainViewModel.NextDueDate(d2029, RecurrenceRule.Yearly, 3, anchorDay: 29));
    }

    [Fact]
    public void NoAnchor_BehavesExactlyAsBefore()
    {
        Assert.Equal(new DateTime(2026, 2, 28), MainViewModel.NextDueDate(new DateTime(2026, 1, 31), RecurrenceRule.Monthly, 1));
        Assert.Equal(new DateTime(2026, 1, 8), MainViewModel.NextDueDate(new DateTime(2026, 1, 1), RecurrenceRule.Weekly, 1, anchorDay: 31));
    }

    [Fact]
    public void EffectiveAnchorDay_TrustsAStoredAnchorOnlyWhileTheDueDateStillMatchesIt()
    {
        Assert.Equal(31, MainViewModel.EffectiveAnchorDay(new DateTime(2026, 2, 28), 31)); // clamped occurrence
        Assert.Equal(31, MainViewModel.EffectiveAnchorDay(new DateTime(2026, 3, 31), 31));
        Assert.Equal(10, MainViewModel.EffectiveAnchorDay(new DateTime(2026, 2, 10), 31)); // user moved it
        Assert.Equal(15, MainViewModel.EffectiveAnchorDay(new DateTime(2026, 2, 15), null));
        Assert.Null(MainViewModel.EffectiveAnchorDay(null, 31));
    }
}

public class RestoreBackupAtomicityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"TaskyTests_{Guid.NewGuid()}");

    public RestoreBackupAtomicityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void RestoreBackup_ReplacesTheDataFile_BacksUpTheOldOne_AndLeavesNoTempFile()
    {
        var store = new TodoStore();
        var dataFile = Path.Combine(_dir, "Tasky.tasky");
        var backupFile = Path.Combine(_dir, "old.tasky");
        var current = new AppState();
        current.Tasks.Add(new TaskItem { Text = "current" });
        store.Save(current, dataFile);
        var old = new AppState();
        old.Tasks.Add(new TaskItem { Text = "from backup" });
        store.Save(old, backupFile);

        store.RestoreBackup(backupFile, dataFile);

        Assert.Equal("from backup", Assert.Single(store.Load(dataFile).Tasks).Text);
        Assert.False(File.Exists(dataFile + ".restore.tmp"));
        Assert.Contains(store.ListBackups(dataFile), b => b.TaskCount == 1); // the pre-restore file
        Assert.True(File.GetLastWriteTimeUtc(dataFile) > DateTime.UtcNow.AddMinutes(-1));
    }
}

public class UpdateChecksumTests
{
    [Fact]
    public void ParseSha256Digest_AcceptsGitHubsFormat_AndNormalisesCase()
    {
        var hex = new string('A', 64);
        Assert.Equal(hex.ToLowerInvariant(), UpdateService.ParseSha256Digest($"sha256:{hex}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("md5:abcdef")]
    [InlineData("sha256:tooshort")]
    [InlineData("sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void ParseSha256Digest_RejectsAnythingElse(string? digest)
        => Assert.Null(UpdateService.ParseSha256Digest(digest));

    [Fact]
    public void ComputeSha256_MatchesAKnownVector()
    {
        var path = Path.Combine(Path.GetTempPath(), $"TaskyTests_{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllText(path, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", UpdateService.ComputeSha256(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class MainViewModelSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"TaskyTests_{Guid.NewGuid()}");

    public MainViewModelSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task LoadFile_ThatFailsToLoad_LeavesTheOpenFileAndItsTasksUntouched()
    {
        var vm = TestViewModels.Create(_dir);
        var goodFile = Path.Combine(_dir, "good.tasky");
        var good = new AppState();
        good.Tasks.Add(new TaskItem { Text = "keep me" });
        new TodoStore().Save(good, goodFile);
        vm.LoadFile(goodFile);

        var corruptFile = Path.Combine(_dir, "corrupt.tasky");
        File.WriteAllText(corruptFile, "{ this is not json");

        Assert.Throws<InvalidDataException>(() => vm.LoadFile(corruptFile));

        Assert.Equal(goodFile, vm.CurrentFilePath);
        Assert.Equal("keep me", Assert.Single(vm.AllTasks).Text);

        // The old failure mode: an empty in-memory state aimed at the corrupt file, which the next
        // autosave then wrote over it.
        vm.AllTasks[0].Text = "edited";
        await vm.FlushPendingSaveAsync();
        Assert.Equal("{ this is not json", File.ReadAllText(corruptFile));
        Assert.Equal("edited", Assert.Single(new TodoStore().Load(goodFile).Tasks).Text);
    }

    [Fact]
    public void CompletingARecurringTask_SpawnsNextOccurrenceWithPriorityAnchorAndLastSortOrder()
    {
        var vm = TestViewModels.Create(_dir, "recurring.tasky");
        vm.AddQuickTask("filler one");
        vm.AddQuickTask("filler two");
        var task = vm.AddQuickTask("Pay rent");
        task.Recurrence = RecurrenceRule.Monthly;
        task.Priority = TaskPriority.High;
        task.DueDate = new DateTime(DateTime.Today.Year + 1, 1, 31, 9, 0, 0);

        task.IsDone = true;

        var spawned = Assert.Single(vm.AllTasks, t => t != task && t.Text == "Pay rent");
        Assert.Equal(TaskPriority.High, spawned.Priority);
        Assert.Equal(31, spawned.RecurrenceAnchorDay);
        Assert.Equal(2, spawned.DueDate!.Value.Month);
        Assert.Equal(vm.AllTasks.Max(t => t.SortOrder), spawned.SortOrder);
        Assert.True(spawned.SortOrder > task.SortOrder);
    }

    [Fact]
    public async Task FailedSave_IsFlaggedAndRetried_InsteadOfSilentlyDropped()
    {
        var file = Path.Combine(_dir, "locked.tasky");
        var vm = TestViewModels.Create(_dir, "locked.tasky");
        var task = vm.AddQuickTask("first");
        await vm.FlushPendingSaveAsync();
        Assert.False(vm.LastSaveFailed);

        // Hold the data file open exclusively so the atomic replace can't land.
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            task.Text = "second";
            await vm.FlushPendingSaveAsync();
            Assert.True(vm.LastSaveFailed);
            Assert.False(string.IsNullOrEmpty(vm.LastSaveError));
        }

        // Lock released: the flush a closing app performs retries and succeeds.
        await vm.FlushPendingSaveAsync();
        Assert.False(vm.LastSaveFailed);
        Assert.Equal("second", Assert.Single(new TodoStore().Load(file).Tasks).Text);
    }
}
