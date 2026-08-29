using System.Text.Json.Serialization;

namespace TodoApp.Services;

public class Settings
{
    public string? LastFilePath { get; set; }
    public string Theme { get; set; } = "Light";
    public bool RemindersEnabled { get; set; } = true;
    // Mirrors Tasky Web's setting-show-done-checkbox (default on there too). Web's version exists
    // because swipe-to-done can make the row checkbox redundant; desktop has no swipe gesture, but
    // the setting is still offered here so preferences stay consistent across platforms - marking
    // done stays reachable via right-click > Mark Completed either way.
    public bool ShowDoneCheckbox { get; set; } = true;
    public bool SidebarCollapsed { get; set; }
    public string? LastSelectedTaskId { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 740;
    public bool WindowMaximized { get; set; }
    public bool IsVerboseLogging { get; set; } = false;
    // Automatic Backups\ snapshots (see TodoStore.BackupExistingFile) - originally fired on every
    // single save, which during active editing meant a new snapshot every ~700ms and only a
    // shallow few-minutes window once the fixed 10-backup cap pruned the rest. Interval-gated and
    // age-retained instead, so backups actually cover a meaningful stretch of time. Daily by
    // default per explicit user preference - note this does mean up to ~24h of edits can sit
    // between snapshots with no fresh recovery point if something goes wrong; a shorter interval
    // is available in Settings > Backup for anyone who wants a tighter safety margin instead.
    public bool AutoBackupEnabled { get; set; } = true;
    public int AutoBackupIntervalMinutes { get; set; } = 1440; // Daily
    public int AutoBackupRetentionDays { get; set; } = 30;
    // ROADMAP.md #135: opt-in, matching Tasky Web's setting-auto-empty-trash default (also off).
    // Uses TaskItem.ModifiedAt as a "trashed at" proxy - see MainViewModel.AutoEmptyTrashIfNeeded.
    public bool AutoEmptyTrashEnabled { get; set; } = false;
    public int AutoEmptyTrashDays { get; set; } = 30;
    public bool IsGoogleDriveEnabled { get; set; } = false;
    // Superseded by GoogleDriveFileIdsByFile - kept only so upgrading installs can migrate
    // their one cached ID forward instead of losing it. No longer written to.
    public string? GoogleDriveFileId { get; set; }
    public string? GoogleDriveFolderId { get; set; }
    // Remote Drive file ID per local .tasky filename (lowercased), so having more than one
    // local data file doesn't make them stomp on each other's synced copy.
    public Dictionary<string, string> GoogleDriveFileIdsByFile { get; set; } = new();
    public string? GoogleDriveAccountEmail { get; set; }
    public string? GoogleDriveClientId { get; set; }
    // Plaintext value used by application code (GoogleDriveService, MainViewModel, settings UI).
    // Never serialized directly - SettingsStore encrypts it into GoogleDriveClientSecretProtected
    // on save and decrypts it back out on load, so settings.json never holds it as plaintext.
    [JsonIgnore]
    public string? GoogleDriveClientSecret { get; set; }
    public string? GoogleDriveClientSecretProtected { get; set; }
    public DateTime? LastGoogleDriveSyncTime { get; set; }
    // Superseded by LastSyncedMediaFilesByFile - every file's attachments used to be diffed
    // against this one shared list, so two different .tasky files' attachments could be
    // mistaken for each other's. Kept only for the one-time migration below.
    public List<string> LastSyncedMediaFiles { get; set; } = new();
    // Which local .tasky filename (lowercased) owns the pre-existing flat "Tasky/Attachments" &
    // "Tasky/InlineImages" layout from before per-file attachment isolation existed. Set once,
    // the first time any file's remote data is resolved rather than newly created (see
    // MainViewModel.MarkLegacyAttachmentsOwnerIfUnset) - every other file gets its own nested
    // subfolder instead of reusing the one that already has real data sitting in it.
    public string? GoogleDriveLegacyAttachmentsFileKey { get; set; }
    // Remote folder ID holding a given file's own "Attachments"/"InlineImages" subfolders - the
    // root "Tasky" folder for GoogleDriveLegacyAttachmentsFileKey, a dedicated per-file subfolder
    // for every other file.
    public Dictionary<string, string> GoogleDriveMediaContainerFolderIdsByFile { get; set; } = new();
    // Per-file replacement for LastSyncedMediaFiles.
    public Dictionary<string, List<string>> LastSyncedMediaFilesByFile { get; set; } = new();
    // Task IDs ReminderScheduler has already notified about this "session" (cleared on file
    // switch via ReminderScheduler.ClearNotified). Persisted so relaunching the app with the same
    // file still open doesn't re-notify everything already due - see review_tasks.md's "Honor
    // time-of-day in reminders and persist notified state" item.
    public List<string> NotifiedTaskIds { get; set; } = new();
    // Gates the first-run Welcome tour (WelcomeWindow) to once ever, same idiom as Tasky Web's
    // `tasky-onboarded` localStorage flag - flips true the first time the tour is shown (whether
    // it ran automatically on first launch or was replayed via About Tasky's "Replay welcome
    // tour" button) so
    // relaunching never shows it again uninvited.
    public bool HasSeenWelcomeTour { get; set; } = false;
    // Gates the once-a-day silent background release check (UpdateService/MainWindow's post-Loaded
    // hook) - off doesn't touch the manual Help > Check for Updates item, same as how
    // AutoBackupEnabled only gates the automatic path and never disables Export/Import themselves.
    public bool AutoCheckForUpdates { get; set; } = true;
    public DateTime? LastUpdateCheckUtc { get; set; }
}
