using System;
using System.IO;
using System.Text.Json;
using TodoApp;

namespace TodoApp.Services;

public class SettingsStore
{
    private readonly string _filePath;
    private int _failureCount;
    private int _batchDepth;
    private Settings? _pendingSettings;

    public SettingsStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Tasky");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "settings.json");
        _failureCount = 0;
    }

    // Lets tests point this at a temp file instead of the real %AppData%\Tasky\settings.json -
    // the parameterless constructor above stays the only one production code uses.
    public SettingsStore(string filePath)
    {
        _filePath = filePath;
        _failureCount = 0;
    }

    // Set by Load() whenever it had to fall back to defaults because settings.json existed but
    // couldn't be read (corrupt JSON, locked file, permissions) - null on a clean load or a
    // first-ever run with no settings.json yet. MainViewModel's constructor checks this right
    // after calling Load() and surfaces it via ThemedMessageBox, so a corrupt settings file is a
    // visible one-time warning instead of a silent reset to defaults (which, since this file also
    // holds the encrypted Google Drive secret/token references, previously looked like Drive had
    // mysteriously disconnected with no explanation).
    public string? LastLoadWarning { get; private set; }

    public Settings Load()
    {
        LastLoadWarning = null;

        if (!File.Exists(_filePath))
            return new Settings();

        try
        {
            var json = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();

            if (!string.IsNullOrEmpty(settings.GoogleDriveClientSecretProtected))
            {
                settings.GoogleDriveClientSecret = SecretProtector.Unprotect(settings.GoogleDriveClientSecretProtected);
            }
            else
            {
                // Pre-encryption settings.json files stored this field as plaintext directly;
                // GoogleDriveClientSecret is now [JsonIgnore] so the deserialize above silently
                // skipped it. Recover it from the raw JSON once so it isn't lost on upgrade - it
                // gets re-saved encrypted the next time Save() runs.
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("GoogleDriveClientSecret", out var legacy) &&
                    legacy.ValueKind == JsonValueKind.String)
                {
                    settings.GoogleDriveClientSecret = legacy.GetString();
                }
            }

            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            App.LogException(ex);
            var backupPath = BackupCorruptFile();
            LastLoadWarning = backupPath is not null
                ? $"Your Tasky settings file was corrupted and couldn't be read, so it's been reset to defaults. Your Google Drive connection, theme, and other preferences will need to be set again.\n\nThe corrupted file was saved to:\n{backupPath}"
                : "Your Tasky settings file was corrupted and couldn't be read, so it's been reset to defaults. Your Google Drive connection, theme, and other preferences will need to be set again.";
            return new Settings();
        }
    }

    // Best-effort single snapshot, not the interval/retention-gated backup system TodoStore uses
    // for the task data file - settings.json corruption is rare enough that one preserved copy
    // per incident is enough to hand-recover from if needed, and a failure here must never mask
    // the original corruption from the caller.
    private string? BackupCorruptFile()
    {
        try
        {
            var backupPath = $"{_filePath}.corrupt-{DateTime.Now:yyyyMMdd_HHmmss}";
            File.Copy(_filePath, backupPath, overwrite: true);
            return backupPath;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsStore", $"Could not back up corrupt settings file: {ex.Message}");
            return null;
        }
    }

    // Settings are convenience state (window position, theme, last file) - never worth blocking
    // or crashing over, so a failed write (locked file, permissions) is logged and swallowed
    // rather than surfaced. This matters beyond just this call site: it's what keeps the
    // MainWindow Closing handler's SaveWindowState call from being able to abort shutdown.
    /// <summary>
    /// Saves settings to disk. Returns false if save failed (locked file, permissions, etc.).
    /// Caller can check return value to notify user after repeated failures.
    /// </summary>
    public bool Save(Settings settings)
    {
        if (_batchDepth > 0)
        {
            _pendingSettings = settings;
            return true;
        }

        return SaveNow(settings);
    }

    private bool SaveNow(Settings settings)
    {
        try
        {
            settings.GoogleDriveClientSecretProtected = SecretProtector.Protect(settings.GoogleDriveClientSecret);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });

            // Same atomic write-then-replace pattern as TodoStore.SaveAsync - writes the new
            // content to a temp file first so a crash/power-loss mid-write can only ever leave a
            // stray .tmp file behind, never a half-written settings.json (which is exactly the
            // corruption Load() above has to recover from).
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(_filePath))
                File.Replace(tempPath, _filePath, null);
            else
                File.Move(tempPath, _filePath);

            _failureCount = 0;
            return true;
        }
        catch (Exception ex)
        {
            _failureCount++;
            App.LogException(ex);
            return false;
        }
    }

    // ROADMAP #126: a single Drive sync pass can call Save() 5+ times (folder resolution, media
    // bookkeeping, file-ID cache, final timestamp), each a DPAPI-protect + full serialize + atomic
    // rewrite. BeginBatch lets a caller collapse all of those into exactly one real write at the
    // end, without changing behavior for every other (immediate, one-off) call site like window
    // state or theme. Depth-counted so nested batch scopes still resolve to a single write.
    public IDisposable BeginBatch()
    {
        _batchDepth++;
        return new BatchScope(this);
    }

    private sealed class BatchScope : IDisposable
    {
        private readonly SettingsStore _store;
        private bool _disposed;

        public BatchScope(SettingsStore store) => _store = store;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _store._batchDepth--;
            if (_store._batchDepth == 0 && _store._pendingSettings is { } pending)
            {
                _store._pendingSettings = null;
                _store.SaveNow(pending);
            }
        }
    }

    /// <summary>
    /// Gets the number of consecutive save failures (resets to 0 on successful save).
    /// </summary>
    public int GetFailureCount() => _failureCount;
}
