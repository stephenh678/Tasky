using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TodoApp.Models;
using TodoApp.Services;

namespace TodoApp.Tests;

// Each test gets its own scratch directory under the OS temp folder so these never touch the
// real %USERPROFILE%\Documents\Tasky data file, and can run in parallel without colliding.
public class TodoStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dataFile;

    public TodoStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TaskyTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _dataFile = Path.Combine(_dir, "Tasky.tasky");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsTasksAndTombstones()
    {
        var store = new TodoStore { AutoBackupEnabled = false };
        var state = new AppState();
        state.Tasks.Add(new TaskItem { Text = "Buy milk", DueDate = new DateTime(2026, 3, 1) });
        state.DeletedTasks.Add(new TaskSyncRecord { TaskId = Guid.NewGuid(), Timestamp = DateTime.Now });

        await store.SaveAsync(state, _dataFile);
        var loaded = await store.LoadAsync(_dataFile);

        Assert.Single(loaded.Tasks);
        Assert.Equal("Buy milk", loaded.Tasks[0].Text);
        Assert.Single(loaded.DeletedTasks);
    }

    // Regression test for the race SaveAsync's snapshot-before-background-serialize exists to
    // close (see AppState.Clone()/TodoStore.SaveAsync's own comments): JsonSerializer.Serialize
    // enumerating the live ObservableCollections directly on a background thread, while something
    // keeps adding/removing tasks and nested Body/Tags entries on another thread, intermittently
    // throws InvalidOperationException ("Collection was modified") - confirmed via an isolated
    // repro against a plain ObservableCollection.
    //
    // Deliberately calls AppState.Clone() directly here rather than racing a mutator against the
    // full SaveAsync from the outside: Clone() itself is synchronous and, in the real app, only
    // ever called from the UI thread with nothing else able to run concurrently with it (WPF's
    // single-threaded dispatcher) - so it can never legitimately race a mutation. A black-box
    // mutator started at the same moment as SaveAsync races that synchronous Clone() step too,
    // which doesn't reflect anything that can actually happen in the app and just makes the test
    // timing-fragile. What actually matters, and is what this asserts, is the guarantee the fix
    // provides: once a snapshot has been taken, serializing it is safe no matter what happens to
    // the live source afterward.
    [Fact]
    public async Task SnapshotTakenBeforeMutation_SerializesSafelyWhileSourceKeepsChanging()
    {
        var state = new AppState();
        for (var i = 0; i < 300; i++)
        {
            var task = new TaskItem { Text = $"Task {i}" };
            task.Tags.Add("tag");
            task.Body.Add(new NoteBlock { Type = NoteBlockType.Text, Text = new string('x', 200_000) });
            state.Tasks.Add(task);
        }

        var snapshot = state.Clone();

        using var cts = new CancellationTokenSource();
        var mutateTask = Task.Run(() =>
        {
            var i = 0;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    state.Tasks.Add(new TaskItem { Text = $"Concurrent {i++}" });
                    state.Tasks[0].Tags.Add($"concurrent-tag-{i}");
                    state.Tasks[0].Body.Add(new NoteBlock { Type = NoteBlockType.Text, Text = "concurrent edit" });
                    if (state.Tasks.Count > 1) state.Tasks.RemoveAt(state.Tasks.Count - 1);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Harmless race between this loop's own Count check and RemoveAt against
                    // itself - not the thing under test (that's the serialize below).
                }
            }
        });

        string json;
        try
        {
            json = await Task.Run(() => JsonSerializer.Serialize(snapshot)); // must not throw
        }
        finally
        {
            cts.Cancel();
            await mutateTask;
        }

        Assert.Contains("\"Task 0\"", json);
        Assert.DoesNotContain("Concurrent", json); // snapshot must reflect pre-mutation state only
    }

    [Fact]
    public async Task LoadAsync_MissingFile_ReturnsEmptyState()
    {
        var store = new TodoStore();
        var loaded = await store.LoadAsync(_dataFile);
        Assert.Empty(loaded.Tasks);
    }

    [Fact]
    public async Task LoadAsync_CorruptJson_ThrowsInvalidDataException()
    {
        await File.WriteAllTextAsync(_dataFile, "{ not valid json ][");
        var store = new TodoStore();

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(_dataFile));
    }

    [Fact]
    public async Task SaveAsync_DoesNotLeaveTempFileBehind()
    {
        var store = new TodoStore { AutoBackupEnabled = false };
        await store.SaveAsync(new AppState(), _dataFile);

        Assert.True(File.Exists(_dataFile));
        Assert.False(File.Exists(_dataFile + ".tmp"));
    }

    [Fact]
    public async Task SaveAsync_WithBackupDisabled_CreatesNoBackup()
    {
        var store = new TodoStore { AutoBackupEnabled = false };
        await store.SaveAsync(new AppState(), _dataFile);
        await store.SaveAsync(new AppState(), _dataFile); // overwrite

        Assert.Empty(store.ListBackups(_dataFile));
    }

    [Fact]
    public async Task SaveAsync_WithBackupEnabled_BacksUpExistingFileBeforeOverwrite()
    {
        var store = new TodoStore { AutoBackupEnabled = true, AutoBackupIntervalMinutes = 1440 };
        await store.SaveAsync(new AppState(), _dataFile); // first save: nothing to back up yet
        Assert.Empty(store.ListBackups(_dataFile));

        await store.SaveAsync(new AppState(), _dataFile); // second save: backs up the first
        Assert.Single(store.ListBackups(_dataFile));
    }

    [Fact]
    public async Task SaveAsync_SecondSaveWithinInterval_DoesNotCreateAnotherBackup()
    {
        var store = new TodoStore { AutoBackupEnabled = true, AutoBackupIntervalMinutes = 1440 };
        await store.SaveAsync(new AppState(), _dataFile);
        await store.SaveAsync(new AppState(), _dataFile); // creates backup #1
        await store.SaveAsync(new AppState(), _dataFile); // within interval: should NOT add backup #2

        Assert.Single(store.ListBackups(_dataFile));
    }

    [Fact]
    public async Task SaveAsync_ZeroMinuteInterval_BacksUpOnEverySave()
    {
        var store = new TodoStore { AutoBackupEnabled = true, AutoBackupIntervalMinutes = 0 };
        await store.SaveAsync(new AppState(), _dataFile);
        await store.SaveAsync(new AppState(), _dataFile);
        await store.SaveAsync(new AppState(), _dataFile);

        Assert.Equal(2, store.ListBackups(_dataFile).Count);
    }

    [Fact]
    public async Task SaveAsync_PrunesBackupsOlderThanRetention()
    {
        var store = new TodoStore { AutoBackupEnabled = true, AutoBackupIntervalMinutes = 0, AutoBackupRetentionDays = 1 };

        var backupsDir = Path.Combine(_dir, "Backups");
        Directory.CreateDirectory(backupsDir);
        var staleName = $"Tasky_{DateTime.Now.AddDays(-30):yyyyMMdd_HHmmss_fff}.tasky";
        await File.WriteAllTextAsync(Path.Combine(backupsDir, staleName), "{}");

        await store.SaveAsync(new AppState(), _dataFile); // no existing data file yet: no backup triggered
        await store.SaveAsync(new AppState(), _dataFile); // now overwrites: triggers pruning + a fresh backup

        var remaining = store.ListBackups(_dataFile);
        Assert.DoesNotContain(remaining, b => b.FilePath.EndsWith(staleName));
    }

    [Fact]
    public void RestoreBackup_CopiesBackupContentOverDataFileAndBacksUpCurrentFirst()
    {
        var store = new TodoStore();
        File.WriteAllText(_dataFile, """{"Tasks":[],"DeletedTasks":[]}""");

        var backupPath = Path.Combine(_dir, "old-snapshot.tasky");
        File.WriteAllText(backupPath, """{"Tasks":[{"Id":"11111111-1111-1111-1111-111111111111","Text":"Restored task"}],"DeletedTasks":[]}""");

        store.RestoreBackup(backupPath, _dataFile);

        var restored = store.Load(_dataFile);
        Assert.Single(restored.Tasks);
        Assert.Equal("Restored task", restored.Tasks[0].Text);
        Assert.Single(store.ListBackups(_dataFile)); // the pre-restore snapshot of the current file
    }

    [Fact]
    public void Load_LegacyNotesLinksAndPhotos_AreMigratedIntoBodyAndCleared()
    {
        var store = new TodoStore { AutoBackupEnabled = false };
        var state = new AppState();
        var task = new TaskItem { Text = "Legacy task", Notes = "old notes" };
        task.Links.Add(new TaskLink { Label = "Docs", Url = "https://example.com" });
        task.Photos.Add(@"C:\fake\photo.jpg");
        state.Tasks.Add(task);
        store.Save(state, _dataFile);

        var loaded = store.Load(_dataFile);
        var loadedTask = loaded.Tasks[0];

        Assert.Equal(3, loadedTask.Body.Count); // text + link + photo blocks
        Assert.Contains(loadedTask.Body, b => b.Type == NoteBlockType.Text && b.Text == "old notes");
        Assert.Contains(loadedTask.Body, b => b.Type == NoteBlockType.Link && b.Url == "https://example.com");
        Assert.Contains(loadedTask.Body, b => b.Type == NoteBlockType.Photo);
        Assert.Empty(loadedTask.Notes);
        Assert.Empty(loadedTask.Links);
        Assert.Empty(loadedTask.Photos);
    }

    // ROADMAP #131: a task that's already been through migration (i.e. every task saved by this
    // app in a long time) has Notes/Links/Photos permanently empty, but they used to still
    // serialize into every save/sync payload regardless ("Notes":"","Links":[],"Photos":[] on
    // every single task). Asserts they're actually gone from the wire format now, not just that
    // the round-trip still works (SaveAsync_ThenLoadAsync_RoundTripsTasksAndTombstones already
    // covers that).
    [Fact]
    public void Save_TaskWithEmptyLegacyFields_OmitsThemFromWireFormat()
    {
        var store = new TodoStore { AutoBackupEnabled = false };
        var state = new AppState();
        state.Tasks.Add(new TaskItem { Text = "Fresh task" });
        store.Save(state, _dataFile);

        var json = File.ReadAllText(_dataFile);

        Assert.DoesNotContain("\"Notes\"", json);
        Assert.DoesNotContain("\"Links\"", json);
        Assert.DoesNotContain("\"Photos\"", json);
    }

    // The other half of #131: a still-populated legacy field must keep serializing (this is a
    // migration path, not a deletion) - only the empty case should be omitted.
    [Fact]
    public void Save_TaskWithPopulatedLegacyFields_StillSerializesThem()
    {
        var store = new TodoStore { AutoBackupEnabled = false };
        var state = new AppState();
        var task = new TaskItem { Text = "Not yet migrated", Notes = "still here" };
        task.Links.Add(new TaskLink { Label = "Docs", Url = "https://example.com" });
        state.Tasks.Add(task);
        store.Save(state, _dataFile);

        var json = File.ReadAllText(_dataFile);

        Assert.Contains("\"Notes\": \"still here\"", json);
        Assert.Contains("\"Links\"", json);
    }

    // ROADMAP #125: ListBackups used to fully JsonSerializer.Deserialize<AppState> every backup
    // file just to read its task count - CountTasksInBackupFile replaces that with a structural
    // Utf8JsonReader scan. Asserts the count it reports is still correct.
    [Fact]
    public async Task ListBackups_ReportsCorrectTaskCountViaLazyScan()
    {
        var store = new TodoStore { AutoBackupEnabled = true, AutoBackupIntervalMinutes = 0 };
        var state = new AppState();
        state.Tasks.Add(new TaskItem { Text = "One" });
        state.Tasks.Add(new TaskItem { Text = "Two" });
        state.Tasks.Add(new TaskItem { Text = "Three" });

        await store.SaveAsync(state, _dataFile); // nothing to back up yet
        await store.SaveAsync(state, _dataFile); // backs up the 3-task snapshot above

        var backup = Assert.Single(store.ListBackups(_dataFile));
        Assert.Equal(3, backup.TaskCount);
    }

    [Fact]
    public async Task ListBackups_MalformedBackupFile_ReportsZeroTaskCountInsteadOfThrowing()
    {
        var store = new TodoStore { AutoBackupEnabled = true, AutoBackupIntervalMinutes = 0 };
        await store.SaveAsync(new AppState(), _dataFile);
        await store.SaveAsync(new AppState(), _dataFile); // creates the backup file to corrupt

        var backupPath = store.ListBackups(_dataFile).Single().FilePath;
        File.WriteAllText(backupPath, "{ not valid json");

        var backup = Assert.Single(store.ListBackups(_dataFile));
        Assert.Equal(0, backup.TaskCount);
    }
}
