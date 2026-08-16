namespace TodoApp.Services;

public class Settings
{
    public string? LastFilePath { get; set; }
    public string Theme { get; set; } = "Light";
    public bool RemindersEnabled { get; set; } = true;
    public bool SidebarCollapsed { get; set; }
    public string? LastSelectedTaskId { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 740;
    public bool WindowMaximized { get; set; }
    public bool IsVerboseLogging { get; set; } = false;
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
    public string? GoogleDriveClientSecret { get; set; }
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
}
