using System.Collections.Generic;
using System.Collections.ObjectModel;
using TodoApp.Models;

namespace TodoApp.Services;

public class AppState
{
    public ObservableCollection<TaskItem> Tasks { get; set; } = new();

    // Tombstones for permanently-deleted tasks, kept so Google Drive's per-task merge can tell a
    // device that simply hasn't pulled a deletion yet apart from a task it should resurrect.
    // Travels with the file itself (unlike Settings.LastSyncedTaskState, which is per-install)
    // since every device doing a merge needs to see the same deletion history.
    public List<TaskSyncRecord> DeletedTasks { get; set; } = new();
}
