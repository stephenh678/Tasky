using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TodoApp.Models;

namespace TodoApp.Services;

/// <summary>
/// Provides data persistence for task application state with automatic backups and retry logic.
/// </summary>
public class TodoStore
{
    private const int MaxBackups = 10;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public static string GetDefaultDataFilePath()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents, "Tasky", "Tasky.tasky");
    }

    /// <summary>
    /// Loads application state from the specified file path.
    /// </summary>
    /// <param name="path">The file path to load from</param>
    /// <returns>The loaded application state or a new AppState if file doesn't exist</returns>
    public AppState Load(string path)
    {
        var state = File.Exists(path) ? ReadFromDisk(path) : new AppState();

        var migrated = false;
        foreach (var task in state.Tasks)
            migrated |= MigrateToBody(task);

        if (migrated)
            Save(state, path);

        return state;
    }

    // A locked/inaccessible file (most commonly OneDrive transiently locking the data file
    // mid-sync, since the default path lives inside a synced Documents folder) is retried a few
    // times rather than immediately falling back to a blank AppState the way a corrupt-JSON file
    // does - unlike bad JSON, a transient lock is likely to clear on its own, and silently
    // starting blank risks a later autosave overwriting the real, still-intact file with
    // nothing. If it's still inaccessible after retries, the IOException/UnauthorizedAccessException
    // is left to propagate - callers decide what "couldn't open the file at all" means for them.
    private static AppState ReadFromDisk(string path)
    {
        const int maxAttempts = 3;
        const int retryDelayMs = 300;
        
        AppLogger.Debug("TodoStore", $"ReadFromDisk: Reading '{path}' (File size: {new FileInfo(path).Length} bytes)");

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var json = File.ReadAllText(path);
                var state = JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
                AppLogger.Info("TodoStore", $"ReadFromDisk: Successfully parsed {state.Tasks.Count} tasks from '{path}'");
                return state;
            }
            catch (JsonException ex)
            {
                AppLogger.Error("TodoStore", $"Corrupted JSON in '{path}'", ex);
                throw new InvalidDataException("The task data file appears to be corrupted and cannot be loaded as valid JSON.", ex);
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                AppLogger.Warn("TodoStore", $"Transient lock reading '{path}' (Attempt {attempt}/{maxAttempts}): {ex.Message}");
                Thread.Sleep(retryDelayMs);
            }
            catch (UnauthorizedAccessException ex) when (attempt < maxAttempts)
            {
                AppLogger.Warn("TodoStore", $"Access denied reading '{path}' (Attempt {attempt}/{maxAttempts}): {ex.Message}");
                Thread.Sleep(retryDelayMs);
            }
        }

        AppLogger.Error("TodoStore", $"Failed to access file '{path}' after {maxAttempts} attempts");
        throw new IOException($"Could not access file '{path}' after {maxAttempts} attempts.");
    }

    /// <summary>
    /// Asynchronously loads application state from the specified file path.
    /// Preferred for new code as it uses proper async/await patterns.
    /// </summary>
    public async Task<AppState> LoadAsync(string path)
    {
        var state = File.Exists(path) ? await ReadFromDiskAsync(path) : new AppState();

        var migrated = false;
        foreach (var task in state.Tasks)
            migrated |= MigrateToBody(task);

        if (migrated)
        {
            AppLogger.Info("TodoStore", $"Migrated legacy task notes to Body format for {state.Tasks.Count} tasks.");
            await SaveAsync(state, path);
        }

        return state;
    }

    private static async Task<AppState> ReadFromDiskAsync(string path)
    {
        const int maxAttempts = 3;
        const int retryDelayMs = 300;

        AppLogger.Debug("TodoStore", $"ReadFromDiskAsync: Reading '{path}'");
        
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var json = await File.ReadAllTextAsync(path);
                var state = JsonSerializer.Deserialize<AppState>(json) ?? new AppState();
                AppLogger.Info("TodoStore", $"ReadFromDiskAsync: Successfully loaded {state.Tasks.Count} tasks from '{path}'");
                return state;
            }
            catch (JsonException ex)
            {
                AppLogger.Error("TodoStore", $"Corrupted JSON in '{path}'", ex);
                throw new InvalidDataException("The task data file appears to be corrupted and cannot be loaded as valid JSON.", ex);
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                AppLogger.Warn("TodoStore", $"Transient lock reading async '{path}' (Attempt {attempt}/{maxAttempts}): {ex.Message}");
                await Task.Delay(retryDelayMs);
            }
            catch (UnauthorizedAccessException ex) when (attempt < maxAttempts)
            {
                AppLogger.Warn("TodoStore", $"Access denied reading async '{path}' (Attempt {attempt}/{maxAttempts}): {ex.Message}");
                await Task.Delay(retryDelayMs);
            }
        }

        AppLogger.Error("TodoStore", $"Failed to access async '{path}' after {maxAttempts} attempts");
        throw new IOException($"Could not access file '{path}' after {maxAttempts} attempts.");
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
    /// <summary>
    /// Synchronously saves application state. Blocks until the save completes.
    /// For new code, prefer SaveAsync for non-blocking I/O.
    /// </summary>
    public void Save(AppState state, string path) => Task.Run(() => SaveAsync(state, path)).GetAwaiter().GetResult();

    /// <summary>
    /// Asynchronously saves application state with atomic write and automatic backups.
    /// Preferred method for I/O operations to keep the UI responsive.
    /// </summary>
    /// <param name="state">The application state to save</param>
    /// <param name="path">The file path to save to</param>
    /// <returns>A task representing the asynchronous save operation</returns>
    public async Task SaveAsync(AppState state, string path)
    {
        await _saveLock.WaitAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            AppLogger.Debug("TodoStore", $"SaveAsync: Starting atomic save of {state.Tasks.Count} tasks to '{path}'");
            
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

            sw.Stop();
            var fi = new FileInfo(path);
            AppLogger.Info("TodoStore", $"SaveAsync: Successfully saved {state.Tasks.Count} tasks ({fi.Length:N0} bytes) in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            AppLogger.Error("TodoStore", $"SaveAsync FAILED for '{path}'", ex);
            throw;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Lists all available backups for the data file, sorted newest first.
    /// </summary>
    /// <param name="dataFilePath">The data file path to list backups for</param>
    /// <returns>A list of available backup snapshots with metadata</returns>
    public List<BackupInfo> ListBackups(string dataFilePath)
    {
        var dir = Path.GetDirectoryName(dataFilePath);
        if (string.IsNullOrEmpty(dir)) return new List<BackupInfo>();

        var backupsDir = Path.Combine(dir, "Backups");
        if (!Directory.Exists(backupsDir)) return new List<BackupInfo>();

        var name = Path.GetFileNameWithoutExtension(dataFilePath);
        var ext = Path.GetExtension(dataFilePath);
        var pattern = new System.Text.RegularExpressions.Regex($@"^{System.Text.RegularExpressions.Regex.Escape(name)}_\d{{8}}_\d{{6}}_\d{{3}}{System.Text.RegularExpressions.Regex.Escape(ext)}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var result = new List<BackupInfo>();
        foreach (var file in Directory.GetFiles(backupsDir, $"{name}_*{ext}"))
        {
            if (!pattern.IsMatch(Path.GetFileName(file))) continue;

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

    /// <summary>
    /// Restores a previous backup snapshot to the data file.
    /// The current file is automatically backed up first, making this operation undoable.
    /// </summary>
    /// <param name="backupFilePath">The path to the backup file to restore</param>
    /// <param name="dataFilePath">The path to the data file to restore to</param>
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

            var pattern = new System.Text.RegularExpressions.Regex($@"^{System.Text.RegularExpressions.Regex.Escape(name)}_\d{{8}}_\d{{6}}_\d{{3}}{System.Text.RegularExpressions.Regex.Escape(ext)}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var stale = Directory.GetFiles(backupsDir, $"{name}_*{ext}")
                .Where(f => pattern.IsMatch(Path.GetFileName(f)))
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(MaxBackups);
            foreach (var old in stale)
            {
                try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Truly best-effort, per the comment above: a backup that can't be written or
            // pruned (locked Backups folder, a path over the length limit, permissions) must
            // never block the actual save that's about to happen right after this returns.
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
