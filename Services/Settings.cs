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
    public string? GoogleDriveFileId { get; set; }
    public string? GoogleDriveAccountEmail { get; set; }
    public string? GoogleDriveClientId { get; set; }
    public string? GoogleDriveClientSecret { get; set; }
    public DateTime? LastGoogleDriveSyncTime { get; set; }
    public List<string> LastSyncedMediaFiles { get; set; } = new();
}
