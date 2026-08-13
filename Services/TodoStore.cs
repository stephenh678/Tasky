using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TodoApp.Models;

namespace TodoApp.Services;

public class TodoStore
{
    private const int MaxBackups = 10;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public static string GetDefaultDataFilePath()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents, "Tasky", "Tasky.tasky");
    }

    public AppState Load(string path)
    {
        AppState state;
        if (!File.Exists(path))
        {
            state = new AppState();
        }
        else
        {
            try
            {
                var json = File.ReadAllText(path);
                state = JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
            }
            catch (JsonException)
            {
                state = new AppState();
            }
        }

        var migrated = false;
        foreach (var task in state.Tasks)
            migrated |= MigrateToBody(task);

        if (migrated)
            Save(state, path);

        return state;
    }

    // Kept for the handful of callers that need the write to have landed before they continue
    // (e.g. the one-time migration inside Load, or the dialog-gated New/Save As commands) - blocks
    // on the async path below rather than duplicating the logic. Critically, this runs SaveAsync
    // via Task.Run rather than awaiting it directly: called from the UI thread, a bare
    // `SaveAsync(...).GetAwaiter().GetResult()` deadlocks - SaveAsync's internal awaits (real async
    // file IO) capture the UI thread's DispatcherSynchronizationContext to resume on, but that
    // thread is the one blocked here waiting for them, so the continuation can never run. Task.Run
    // moves the whole async chain onto a thread-pool thread first, where there's no captured UI
    // context to deadlock against.
    public void Save(AppState state, string path) => Task.Run(() => SaveAsync(state, path)).GetAwaiter().GetResult();

    // Writes to a temp file and atomically swaps it into place (File.Replace/Move) instead of
    // overwriting the live file directly, so a crash or an OneDrive-sync file lock mid-write can't
    // leave a half-written data file behind. Runs off the UI thread and serializes concurrent
    // callers (nearly every task edit triggers a save) so two saves can't race over the same temp
    // file.
    public async Task SaveAsync(AppState state, string path)
    {
        await _saveLock.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(path))
                await Task.Run(() => BackupExistingFile(path));

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);

            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // Lists the rolling backups for a data file, newest first, with a task count read from
    // each snapshot so the restore dialog can show something more useful than a bare timestamp.
    public List<BackupInfo> ListBackups(string dataFilePath)
    {
        var dir = Path.GetDirectoryName(dataFilePath);
        if (string.IsNullOrEmpty(dir)) return new List<BackupInfo>();

        var backupsDir = Path.Combine(dir, "Backups");
        if (!Directory.Exists(backupsDir)) return new List<BackupInfo>();

        var name = Path.GetFileNameWithoutExtension(dataFilePath);
        var ext = Path.GetExtension(dataFilePath);

        var result = new List<BackupInfo>();
        foreach (var file in Directory.GetFiles(backupsDir, $"{name}_*{ext}"))
        {
            var count = 0;
            try
            {
                var json = File.ReadAllText(file);
                count = JsonSerializer.Deserialize<AppState>(json)?.Tasks.Count ?? 0;
            }
            catch (JsonException)
            {
            }

            result.Add(new BackupInfo { FilePath = file, Timestamp = File.GetLastWriteTime(file), TaskCount = count });
        }

        return result.OrderByDescending(b => b.Timestamp).ToList();
    }

    // Restoring is itself destructive to whatever is currently on disk, so the current file gets
    // snapshotted first - a restore is always undoable by restoring again.
    public void RestoreBackup(string backupFilePath, string dataFilePath)
    {
        if (File.Exists(dataFilePath))
            BackupExistingFile(dataFilePath);
        File.Copy(backupFilePath, dataFilePath, overwrite: true);
    }

    // Snapshots the file as it was right before each overwrite, so a bad edit or a crash mid-write
    // is recoverable by hand from Backups\ next to the data file. Best-effort: a backup failure
    // must never block the actual save.
    private static void BackupExistingFile(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            var backupsDir = Path.Combine(dir, "Backups");
            Directory.CreateDirectory(backupsDir);

            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var backupPath = Path.Combine(backupsDir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}");
            File.Copy(path, backupPath, overwrite: false);

            var stale = Directory.GetFiles(backupsDir, $"{name}_*{ext}")
                .OrderByDescending(f => f)
                .Skip(MaxBackups);
            foreach (var old in stale)
            {
                try { File.Delete(old); } catch (IOException) { }
            }
        }
        catch (IOException)
        {
        }
    }

    // One-time migration: older saves kept Notes/Links/Photos as separate fields.
    // The editor now works entirely off Body, so fold any legacy content in once, then clear it.
    private static bool MigrateToBody(TaskItem task)
    {
        if (task.Body.Count > 0) return false;
        if (string.IsNullOrWhiteSpace(task.Notes) && task.Links.Count == 0 && task.Photos.Count == 0) return false;

        if (!string.IsNullOrWhiteSpace(task.Notes))
            task.Body.Add(new NoteBlock { Type = NoteBlockType.Text, Text = task.Notes });

        foreach (var link in task.Links)
            task.Body.Add(new NoteBlock { Type = NoteBlockType.Link, LinkLabel = link.Label, Url = link.Url });

        foreach (var photoPath in task.Photos)
            task.Body.Add(new NoteBlock { Type = NoteBlockType.Photo, PhotoPath = photoPath });

        task.Notes = string.Empty;
        task.Links.Clear();
        task.Photos.Clear();
        return true;
    }
}
