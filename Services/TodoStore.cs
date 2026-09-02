using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TodoApp.Models;

namespace TodoApp.Services;

/// <summary>
/// Provides data persistence for task application state with automatic backups and retry logic.
/// </summary>
public class TodoStore
{
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    // ROADMAP #129: SaveAsync used to allocate a fresh JsonSerializerOptions on every call (every
    // autosave, on a 700ms debounce while editing) just to set WriteIndented - hoisted to a shared
    // static since the options themselves never vary between calls.
    private static readonly JsonSerializerOptions SaveJsonOptions = new() { WriteIndented = true };

    // ROADMAP #62: ListBackups/BackupExistingFile used to each build a new Regex (with
    // Regex.Escape(name) baked into the pattern) on every single call. One generic, compiled
    // pattern shared by both instead - it captures the name/timestamp/extension structurally, and
    // the caller compares the matched name/extension groups against the target file in plain code,
    // so nothing is allocated or compiled per call.
    private static readonly Regex BackupFileNameRegex = new(
        @"^(?<name>.+)_(?<ts>\d{8}_\d{6}_\d{3})(?<ext>\.[^.]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryMatchBackupFileName(string fileName, string expectedName, string expectedExt, out string timestampToken)
    {
        timestampToken = string.Empty;
        var match = BackupFileNameRegex.Match(fileName);
        if (!match.Success) return false;
        if (!string.Equals(match.Groups["name"].Value, expectedName, StringComparison.OrdinalIgnoreCase)) return false;

        var matchedExt = match.Groups["ext"].Success ? match.Groups["ext"].Value : string.Empty;
        if (!string.Equals(matchedExt, expectedExt, StringComparison.OrdinalIgnoreCase)) return false;

        timestampToken = match.Groups["ts"].Value;
        return true;
    }

    // Mirrors Settings.AutoBackup* - MainViewModel applies the loaded/edited settings here rather
    // than this class reading Settings directly, so TodoStore (a plain file-IO service used well
    // below the ViewModel layer) doesn't need to know the Settings type at all. Defaults match
    // Settings.cs's own defaults for a TodoStore used standalone (e.g. in a future test).
    public bool AutoBackupEnabled { get; set; } = true;
    public int AutoBackupIntervalMinutes { get; set; } = 1440;
    public int AutoBackupRetentionDays { get; set; } = 30;

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
        // Snapshotted synchronously here, on whatever thread called SaveAsync (the UI thread for
        // the hot edit-triggered-save path) - state.Tasks and its nested Body/Tags/ChecklistItems
        // are live ObservableCollections still bound to WPF, which can keep being mutated (typing,
        // toggling, drag-drop...) while a save is in flight. JsonSerializer.Serialize below runs
        // on a background thread; enumerating those same live collections from there would risk
        // an InvalidOperationException from a concurrent Add/Remove hitting mid-enumeration. This
        // copy happens-before any background work starts, so nothing else can still be mutating
        // the version actually being written.
        var snapshot = state.Clone();

        await _saveLock.WaitAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            AppLogger.Debug("TodoStore", $"SaveAsync: Starting atomic save of {snapshot.Tasks.Count} tasks to '{path}'");

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(path) && AutoBackupEnabled)
                await Task.Run(() => BackupExistingFile(path));

            var json = await Task.Run(() => JsonSerializer.Serialize(snapshot, SaveJsonOptions));
            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, json);

            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);

            sw.Stop();
            var fi = new FileInfo(path);
            // ROADMAP #127: this fires on every single save (autosave runs on a 700ms debounce
            // while editing), so it's routine noise rather than a notable event - demoted from
            // Info to Debug so it doesn't dominate the default (non-verbose) log.
            AppLogger.Debug("TodoStore", $"SaveAsync: Successfully saved {snapshot.Tasks.Count} tasks ({fi.Length:N0} bytes) in {sw.ElapsedMilliseconds}ms");
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

        var result = new List<BackupInfo>();
        foreach (var file in Directory.GetFiles(backupsDir, $"{name}_*{ext}"))
        {
            if (!TryMatchBackupFileName(Path.GetFileName(file), name, ext, out _)) continue;

            result.Add(new BackupInfo { FilePath = file, Timestamp = File.GetLastWriteTime(file), TaskCount = CountTasksInBackupFile(file) });
        }

        return result.OrderByDescending(b => b.Timestamp).ToList();
    }

    // ROADMAP #125: ListBackups used to fully JsonSerializer.Deserialize<AppState> every backup
    // file (up to MaxBackupSafetyCount = 500 of them) just to read one number off it for the
    // restore-backup dialog. Utf8JsonReader scans for the "Tasks" array and counts its elements
    // structurally without ever materializing a TaskItem, so opening that dialog with a long
    // backup history doesn't pay for 500 full object-graph deserializations up front.
    private static int CountTasksInBackupFile(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var reader = new Utf8JsonReader(bytes);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("Tasks"))
                {
                    reader.Read();
                    return reader.TokenType == JsonTokenType.StartArray ? CountArrayElements(ref reader) : 0;
                }
            }
        }
        catch (Exception)
        {
            // Malformed/unreadable backup file - matches the prior behavior of counting it as 0
            // rather than letting the whole dialog fail to open over one bad snapshot.
        }
        return 0;
    }

    private static int CountArrayElements(ref Utf8JsonReader reader)
    {
        var count = 0;
        var nestDepth = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    if (nestDepth == 0) count++;
                    nestDepth++;
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (nestDepth == 0) return count;
                    nestDepth--;
                    break;
                default:
                    if (nestDepth == 0) count++;
                    break;
            }
        }
        return count;
    }

    /// <summary>
    /// Restores a previous backup snapshot to the data file.
    /// The current file is automatically backed up first, making this operation undoable.
    /// </summary>
    /// <param name="backupFilePath">The path to the backup file to restore</param>
    /// <param name="dataFilePath">The path to the data file to restore to</param>
    public void RestoreBackup(string backupFilePath, string dataFilePath)
    {
        // force: true - restoring is a deliberate, risky action the confirmation dialog promises
        // is itself undoable by backing up first. That guarantee has to hold even if the user has
        // auto-backup turned off, or the last periodic snapshot was recent enough that the normal
        // interval gate below would otherwise skip it.
        if (File.Exists(dataFilePath))
            BackupExistingFile(dataFilePath, force: true);
        File.Copy(backupFilePath, dataFilePath, overwrite: true);
    }

    // A generous ceiling independent of AutoBackupRetentionDays - age-based retention alone has no
    // upper bound on file COUNT (a short interval + a long retention could otherwise accumulate
    // thousands of snapshots), so this caps it regardless of how those two settings are combined.
    // Far above what any reasonable interval/retention combination should normally reach, so it's
    // a safety net, not a replacement for age-based pruning.
    private const int MaxBackupSafetyCount = 500;

    // Snapshots the file as it was right before an overwrite, so a bad edit or a crash mid-write
    // is recoverable by hand from Backups\ next to the data file. Best-effort: a backup failure
    // must never block the actual save. Interval-gated (skip if the newest existing snapshot is
    // still within AutoBackupIntervalMinutes) and age-retained (prune anything older than
    // AutoBackupRetentionDays) rather than a fixed "keep the last N" count - what "10" means
    // shifts with the interval itself, so a duration is the only definition that stays meaningful
    // regardless of how often backups actually run.
    private void BackupExistingFile(string path, bool force = false)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            var backupsDir = Path.Combine(dir, "Backups");
            Directory.CreateDirectory(backupsDir);

            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);

            // Each backup's own creation time is parsed from its filename rather than read via
            // File.GetLastWriteTime: File.Copy below preserves the SOURCE file's timestamp on the
            // copy instead of stamping "now" onto it, and this method runs before `path` is
            // overwritten - so the filesystem mtime a freshly-created backup gets is actually the
            // PREVIOUS save's write time, one full cycle stale. That staleness made the interval
            // gate below compare against a timestamp that was always ~2 cycles old, which defeats
            // it entirely (confirmed: it degrades to creating a new backup on nearly every save,
            // regardless of AutoBackupIntervalMinutes). The filename's own {yyyyMMdd_HHmmss_fff},
            // captured fresh each time a backup is actually made, has no such staleness problem.
            var existing = Directory.GetFiles(backupsDir, $"{name}_*{ext}")
                .Select(f =>
                {
                    var matched = TryMatchBackupFileName(Path.GetFileName(f), name, ext, out var timestampToken);
                    return matched && DateTime.TryParseExact(timestampToken, "yyyyMMdd_HHmmss_fff",
                        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var ts)
                        ? (Path: f, Timestamp: ts)
                        : default;
                })
                .Where(x => x.Path is not null)
                .OrderByDescending(x => x.Timestamp)
                .ToList();

            if (!force && existing.Count > 0 && DateTime.Now - existing[0].Timestamp < TimeSpan.FromMinutes(AutoBackupIntervalMinutes))
                return;

            var backupPath = Path.Combine(backupsDir, $"{name}_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}");
            File.Copy(path, backupPath, overwrite: false);

            var cutoff = DateTime.Now.AddDays(-AutoBackupRetentionDays);
            var stale = existing.Where(x => x.Timestamp < cutoff).Select(x => x.Path)
                .Union(existing.Skip(MaxBackupSafetyCount).Select(x => x.Path));
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
