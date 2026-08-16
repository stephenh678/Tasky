using System;

namespace TodoApp.Models;

// A lightweight (task ID, timestamp) pair used two ways by Google Drive sync's per-task merge:
// as a tombstone (AppState.DeletedTasks - Timestamp is when the task was deleted, so a device
// that hasn't pulled the deletion yet doesn't resurrect it), and as a last-synced snapshot
// (Settings.LastSyncedTaskState - Timestamp is each task's ModifiedAt as of the last successful
// sync, letting a 3-way merge tell "edited since last sync" apart from "never seen this before").
public class TaskSyncRecord
{
    public Guid TaskId { get; set; }
    public DateTime Timestamp { get; set; }
}
