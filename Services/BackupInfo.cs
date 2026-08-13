using System;

namespace TodoApp.Services;

public class BackupInfo
{
    public string FilePath { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public int TaskCount { get; set; }
}
