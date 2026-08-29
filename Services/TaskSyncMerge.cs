using System;
using System.Collections.Generic;
using System.Linq;
using TodoApp.Models;

namespace TodoApp.Services;

// The decision result of a merge: which tasks to add/update/remove and which tombstones are new,
// with no reference to any UI-bound collection. MainViewModel.MergeRemoteState applies this plan
// to AllTasks/AttachTask/DetachTask/SelectedTask; this type and TaskSyncMerge below only decide
// WHAT should happen, never touch WPF-bound state, so they're plain, fast, dependency-free to
// unit test.
public readonly record struct TaskMergePlan(
    List<TaskItem> TasksToAdd,
    List<(TaskItem Local, TaskItem Remote)> TasksToUpdate,
    List<TaskItem> TasksToRemove,
    List<TaskSyncRecord> TombstonesToAdd,
    List<TaskItem> ConflictedCopiesToAdd);

/// <summary>
/// Pure per-task merge logic for Google Drive sync, kept separate from MainViewModel so it can be
/// unit tested without constructing a full ViewModel (which loads real settings/theme on
/// construction).
/// </summary>
public static class TaskSyncMerge
{
    // Per-task merge for Google Drive sync, replacing a whole-file "which copy is newer" guess.
    // The whole-file version couldn't tell "clean update from another device" apart from "real
    // conflict" without misfiring, because a single timestamp on the whole file conflates every
    // task's history into one number. Per task, the picture is much clearer: a task missing from
    // one side either belongs there (bring it in) or was deleted (a tombstone says when, so a
    // device that's simply behind doesn't resurrect it) - and a task edited on only one side since
    // they last agreed is always safe to adopt. The one thing this still can't fully reconcile is
    // the same task edited on both sides in the same window - that's genuine field-level merging
    // (CRDT territory), out of scope here, so remote's edit still wins the original task ID, but
    // (unlike before - see ROADMAP.md #119) local's about-to-be-overwritten edit is kept too, as a
    // separate "(conflicted copy)" task, via ConflictedCopiesToAdd - nothing just vanishes.
    //
    // lastSyncTime (device-local, converted to UTC by the caller - see MainViewModel) is what
    // distinguishes a genuine conflict from an ordinary stale-device update: if local hasn't
    // touched a task since the two sides last agreed, remote's newer edit is just new information,
    // not a competing edit. Null (never synced before) means there's no baseline to compare
    // against, so nothing this call sees can count as a conflict yet.
    public static TaskMergePlan ComputeMergePlan(
        IEnumerable<TaskItem> localTasks,
        IEnumerable<TaskItem> remoteTasks,
        IEnumerable<TaskSyncRecord> localTombstones,
        IEnumerable<TaskSyncRecord> remoteTombstones,
        DateTime? lastSyncTimeUtc = null)
    {
        var localById = localTasks.ToDictionary(t => t.Id);
        var remoteById = remoteTasks.ToDictionary(t => t.Id);
        var localTombstonesById = localTombstones.ToDictionary(r => r.TaskId, r => r.Timestamp);
        var remoteTombstonesById = remoteTombstones.ToDictionary(r => r.TaskId, r => r.Timestamp);

        var toAdd = new List<TaskItem>();
        var toUpdate = new List<(TaskItem Local, TaskItem Remote)>();
        var toRemove = new List<TaskItem>();
        var conflictedCopies = new List<TaskItem>();

        // Remote-only tasks: bring them in, unless this device already deleted the same ID and
        // remote's copy predates that deletion (i.e. remote just hasn't learned about it yet).
        foreach (var (id, remoteTask) in remoteById)
        {
            if (localById.ContainsKey(id)) continue;

            if (localTombstonesById.TryGetValue(id, out var deletedAt) && remoteTask.ModifiedAt <= deletedAt)
                continue;

            toAdd.Add(remoteTask);
        }

        // Local-only tasks: leave them (they'll upload as-is), unless another device already
        // deleted the same ID and this device hasn't touched it since that deletion.
        foreach (var (id, localTask) in localById)
        {
            if (remoteById.ContainsKey(id)) continue;

            if (remoteTombstonesById.TryGetValue(id, out var deletedAt) && localTask.ModifiedAt <= deletedAt)
                toRemove.Add(localTask);
        }

        // Present on both sides: the newer edit to THIS task wins the original ID. If local ALSO
        // changed since the last sync, that losing edit would otherwise just disappear - keep it
        // as a conflicted copy instead.
        foreach (var (id, remoteTask) in remoteById)
        {
            if (!localById.TryGetValue(id, out var localTask)) continue;
            if (remoteTask.ModifiedAt <= localTask.ModifiedAt) continue;

            if (lastSyncTimeUtc.HasValue && localTask.ModifiedAt > lastSyncTimeUtc.Value)
                conflictedCopies.Add(CreateConflictedCopy(localTask));

            toUpdate.Add((localTask, remoteTask));
        }

        // Tombstones union both ways, so a third device merging later learns about every
        // deletion recorded anywhere, not just the ones this device made itself.
        var mergedTombstoneIds = new HashSet<Guid>(localTombstonesById.Keys);
        var tombstonesToAdd = new List<TaskSyncRecord>();
        foreach (var remoteTombstone in remoteTombstones)
        {
            if (mergedTombstoneIds.Add(remoteTombstone.TaskId))
                tombstonesToAdd.Add(remoteTombstone);
        }

        return new TaskMergePlan(toAdd, toUpdate, toRemove, tombstonesToAdd, conflictedCopies);
    }

    // A new task, not a shared identity with the losing edit - the original ID stays with
    // whichever edit won (see the loop above), so this copy needs its own so it doesn't collide
    // with it on the very next sync.
    private static TaskItem CreateConflictedCopy(TaskItem losingEdit)
    {
        var copy = losingEdit.Clone();
        copy.Id = Guid.NewGuid();
        copy.Text = $"{losingEdit.Text} (conflicted copy)";
        copy.CreatedAt = DateTime.UtcNow;
        copy.ModifiedAt = DateTime.UtcNow;
        return copy;
    }

    // Copies remote's fields onto an existing local task in place (rather than swapping in the
    // remote TaskItem instance) so anything referencing the local instance - AllTasks' identity,
    // the currently-open TaskDetailViewModel if this task is selected - keeps working across a
    // merge update.
    public static void ApplyTaskFields(TaskItem target, TaskItem source)
    {
        target.Text = source.Text;
        target.IsDone = source.IsDone;
        target.IsClosed = source.IsClosed;
        target.IsPinned = source.IsPinned;
        target.DueDate = source.DueDate;
        target.Recurrence = source.Recurrence;
        target.RecurrenceInterval = source.RecurrenceInterval;
        target.Priority = source.Priority;

        target.Tags.Clear();
        foreach (var tag in source.Tags) target.Tags.Add(tag);

        target.Body.Clear();
        foreach (var block in source.Body) target.Body.Add(block);

        target.ModifiedAt = source.ModifiedAt;
    }

    // A task can legitimately be tombstoned more than once in its lifetime - delete, then a later
    // edit on another device revives it (an intentional part of the merge - see ComputeMergePlan),
    // then it gets deleted again. Keep the existing tombstone's latest timestamp instead of a
    // second entry for the same TaskId: ComputeMergePlan builds a Dictionary keyed by TaskId from
    // this list, which throws on a duplicate key - a second entry wouldn't just pick the "wrong"
    // timestamp, it would crash the sync outright, and keep crashing on every retry.
    //
    // ROADMAP.md #140: DeletedTasks used to grow unbounded - every permanent delete added a record
    // that got merged and re-uploaded forever. Tombstones older than RetentionWindow are now
    // dropped here too, since this is the one place both the local (on load) and remote (right
    // after download) tombstone lists already pass through before a merge. The accepted tradeoff:
    // a device offline for longer than the window, still holding a task some other device deleted
    // that long ago, resurrects it on reconnect instead of finding a tombstone to tell it not to -
    // must match docs/js/sync.js's deduplicateTombstones exactly, or a device on the older rule
    // would keep re-adding a tombstone the other side has already dropped, right back into a mismatch.
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(90);

    public static List<TaskSyncRecord> DeduplicateTombstones(List<TaskSyncRecord> tombstones, DateTime? nowUtc = null)
    {
        var cutoff = (nowUtc ?? DateTime.UtcNow) - RetentionWindow;
        return tombstones
            .Where(r => r.Timestamp >= cutoff)
            .GroupBy(r => r.TaskId)
            .Select(g => g.OrderByDescending(r => r.Timestamp).First())
            .ToList();
    }
}
