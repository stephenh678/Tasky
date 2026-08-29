using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

    // Saved sidebar "Views" (named search-box queries) - synced like tasks/tags, unlike Web's
    // per-browser localStorage copy. DeletedSavedViewIds is the same tombstone idea as
    // DeletedTasks: without it, a view deleted on one device would resurrect the next time a
    // device with a stale, not-yet-synced copy uploads.
    public List<SavedView> SavedViews { get; set; } = new();
    public List<string> DeletedSavedViewIds { get; set; } = new();

    // Write-only snapshot for background JSON serialization (see TodoStore.SaveAsync) - see
    // TaskItem.Clone()'s own comment for why this only needs to go collection-deep, not a full
    // field-by-field deep clone of every mutable object.
    public AppState Clone() => new()
    {
        Tasks = new ObservableCollection<TaskItem>(Tasks.Select(t => t.Clone())),
        DeletedTasks = new List<TaskSyncRecord>(DeletedTasks),
        SavedViews = new List<SavedView>(SavedViews),
        DeletedSavedViewIds = new List<string>(DeletedSavedViewIds),
    };
}
