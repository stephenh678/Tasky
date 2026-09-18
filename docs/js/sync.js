// Port of MainViewModel.cs's per-task 3-way merge (MergeRemoteState / ApplyTaskFields /
// DeduplicateTombstones), kept behaviorally identical so a file synced by the web app merges the
// same way a desktop client merging that same file would. See the C# comments for the full
// rationale; kept brief here to avoid drifting out of sync with the original as comments.
import { parseDotNetDate, newGuid, nowDotNet } from './model.js?v=30';

// ROADMAP.md #140: DeletedTasks used to grow unbounded on both platforms - every permanent delete
// added a record that got merged and re-uploaded forever. Tombstones older than RETENTION_MS are
// now dropped here too, mirroring TaskSyncMerge.cs's DeduplicateTombstones exactly (same 90-day
// window, same accepted resurrection tradeoff for a device offline longer than that - see the C#
// comment for the full rationale). Must match the desktop side precisely, or a device on the
// older rule would keep re-adding a tombstone the other side has already dropped.
const RETENTION_MS = 90 * 24 * 60 * 60 * 1000;

export function deduplicateTombstones(tombstones, now = new Date()) {
  const cutoff = now.getTime() - RETENTION_MS;
  const byId = new Map();
  for (const t of tombstones) {
    // A malformed record (hand-edited file, partial write) used to throw here and take the whole
    // load down with it - it carries nothing usable, so drop it. Mirrors what desktop's
    // deserializer effectively does with a null Timestamp (DateTime.MinValue, aged out).
    if (!t || !t.TaskId || !t.Timestamp) continue;
    if (parseDotNetDate(t.Timestamp).getTime() < cutoff) continue;
    const existing = byId.get(t.TaskId);
    if (!existing || parseDotNetDate(t.Timestamp) > parseDotNetDate(existing.Timestamp)) {
      byId.set(t.TaskId, t);
    }
  }
  return [...byId.values()];
}

/**
 * Boot-time reconciliation for a local snapshot (snapshot.js) that a previous session left dirty -
 * i.e. edits that never reached Drive because the tab was killed or the device was offline. The
 * snapshot is treated exactly like this device's live state and the fresh download exactly like
 * any other device's changes: the same 3-way merge, keyed on lastSyncTime, so the offline edits
 * win where remote didn't move and become "(conflicted copy)" tasks where it did. Returns the
 * reconciled state (the snapshot object, mutated in place) for the caller to adopt and upload.
 */
export function reconcileLocalSnapshot(snapshotState, remoteState, lastSyncTime) {
  const result = mergeRemoteState(snapshotState, remoteState, lastSyncTime);
  mergeSavedViews(snapshotState, remoteState);
  return { state: snapshotState, ...result };
}

function applyTaskFields(target, source) {
  target.Text = source.Text;
  target.IsDone = source.IsDone;
  target.IsClosed = source.IsClosed;
  target.IsPinned = source.IsPinned;
  // Mirrors TaskSyncMerge.ApplyTaskFields' `target.SortOrder = source.SortOrder`. Missing here
  // meant a task whose content remote won kept this device's stale manual position.
  target.SortOrder = Number(source.SortOrder) || 0;
  target.DueDate = source.DueDate;
  target.Recurrence = source.Recurrence;
  target.RecurrenceInterval = source.RecurrenceInterval ?? 1;
  target.Priority = source.Priority;
  // A remote task missing Tags/Body entirely (an old pre-migration desktop file, or a
  // hand-edited/partially-written one) used to throw here, mid-merge, after some other tasks in
  // the same pass had already been mutated in place - fall back to empty rather than crash.
  target.Tags = Array.isArray(source.Tags) ? [...source.Tags] : [];
  target.Body = Array.isArray(source.Body) ? source.Body.map((b) => ({ ...b })) : [];
  target.ModifiedAt = source.ModifiedAt;
}

// A new task, not a shared identity with the losing edit - see mergeRemoteState's matching
// comment. Mirrors TaskSyncMerge.cs's CreateConflictedCopy.
function createConflictedCopy(losingEdit) {
  return {
    ...losingEdit,
    Id: newGuid(),
    Text: `${losingEdit.Text} (conflicted copy)`,
    CreatedAt: nowDotNet(),
    ModifiedAt: nowDotNet(),
  };
}

/**
 * Merges remoteState into localState IN PLACE (mutates localState.Tasks / localState.DeletedTasks)
 * and returns { added, updated, removed, conflicted } counts, matching MergeRemoteState's return
 * shape, plus updatedIds / removedIds (the task IDs behind the `updated` and `removed` counts) -
 * app.js needs those to tell whether the task currently open in the editor was one of them, since
 * applyTaskFields swaps in brand-new Body objects and a removed task disappears from findTask()
 * entirely; either way the editor's live DOM would otherwise keep writing into orphaned objects.
 * lastSyncTime (a Date, or null/undefined if never synced before) is what distinguishes a
 * genuine same-task-both-sides conflict from an ordinary stale-device update - see
 * TaskSyncMerge.ComputeMergePlan's matching comment on the desktop side.
 */
export function mergeRemoteState(localState, remoteState, lastSyncTime = null) {
  const localById = new Map(localState.Tasks.map((t) => [t.Id, t]));
  const remoteById = new Map(remoteState.Tasks.map((t) => [t.Id, t]));
  const localTombstones = new Map(localState.DeletedTasks.map((r) => [r.TaskId, parseDotNetDate(r.Timestamp)]));
  const remoteTombstones = new Map(remoteState.DeletedTasks.map((r) => [r.TaskId, parseDotNetDate(r.Timestamp)]));

  let added = 0;
  let updated = 0;
  let removed = 0;
  let conflicted = 0;
  const updatedIds = [];
  const removedIds = [];

  // Remote-only tasks: bring them in, unless this device already deleted the same ID and
  // remote's copy predates that deletion.
  for (const [id, remoteTask] of remoteById) {
    if (localById.has(id)) continue;
    const deletedAt = localTombstones.get(id);
    if (deletedAt && parseDotNetDate(remoteTask.ModifiedAt) <= deletedAt) continue;
    localState.Tasks.push(remoteTask);
    added++;
  }

  // Local-only tasks: leave them, unless another device already deleted the same ID and this
  // device hasn't touched it since that deletion.
  const toRemove = new Set();
  for (const [id, localTask] of localById) {
    if (remoteById.has(id)) continue;
    const deletedAt = remoteTombstones.get(id);
    if (deletedAt && parseDotNetDate(localTask.ModifiedAt) <= deletedAt) {
      toRemove.add(id);
      removedIds.push(id);
      removed++;
    }
  }
  if (toRemove.size > 0) {
    localState.Tasks = localState.Tasks.filter((t) => !toRemove.has(t.Id));
  }

  // Present on both sides: the newer edit wins the original ID. If local ALSO changed since the
  // last sync, that losing edit would otherwise just disappear - keep it as a conflicted copy.
  const newConflictedCopies = [];
  for (const [id, remoteTask] of remoteById) {
    const localTask = localById.get(id);
    if (!localTask) continue;
    if (parseDotNetDate(remoteTask.ModifiedAt) <= parseDotNetDate(localTask.ModifiedAt)) continue;
    if (lastSyncTime && parseDotNetDate(localTask.ModifiedAt) > lastSyncTime) {
      newConflictedCopies.push(createConflictedCopy(localTask));
      conflicted++;
    }
    applyTaskFields(localTask, remoteTask);
    updatedIds.push(id);
    updated++;
  }
  localState.Tasks.push(...newConflictedCopies);

  // Tombstones union both ways, so a third device merging later learns about every deletion
  // recorded anywhere.
  const mergedIds = new Set(localState.DeletedTasks.map((r) => r.TaskId));
  for (const remoteTombstone of remoteState.DeletedTasks) {
    if (!mergedIds.has(remoteTombstone.TaskId)) {
      mergedIds.add(remoteTombstone.TaskId);
      localState.DeletedTasks.push(remoteTombstone);
    }
  }

  // After the field updates above, so remote's arrangement wins over any SortOrder applyTaskFields
  // just copied.
  localState.TasksOrderModifiedAt = mergeTaskOrder(
    localState.Tasks, remoteState.Tasks, localState.TasksOrderModifiedAt, remoteState.TasksOrderModifiedAt);

  return { added, updated, removed, conflicted, updatedIds, removedIds };
}

/**
 * Port of TaskSyncMerge.MergeTaskOrder (Services/TaskSyncMerge.cs).
 *
 * Manual ordering is a property of the LIST, not of any one task, so it can't ride on a task's
 * ModifiedAt: a drag renumbers every task, and bumping each one's ModifiedAt would make this device
 * win a newer-wins merge on every field of every task. Desktop's Task_PropertyChanged therefore
 * ignores SortOrder - which left the opposite hole, where a pure reorder made nothing "newer" and
 * every device silently discarded it. AppState.TasksOrderModifiedAt closes that: whole-arrangement
 * newer-wins, with null ("never reordered") losing to any real timestamp.
 *
 * Mutates the SortOrder of tasks in localTasks when remote wins. Returns the winning timestamp.
 */
export function mergeTaskOrder(localTasks, remoteTasks, localOrderModifiedAt, remoteOrderModifiedAt) {
  if (!remoteOrderModifiedAt) return localOrderModifiedAt ?? null;
  const remoteAt = parseDotNetDate(remoteOrderModifiedAt);
  if (localOrderModifiedAt) {
    const localAt = parseDotNetDate(localOrderModifiedAt);
    if (localAt >= remoteAt) return localOrderModifiedAt;
  }

  const remoteOrderById = new Map(remoteTasks.map((t) => [t.Id, Number(t.SortOrder) || 0]));
  for (const localTask of localTasks) {
    if (remoteOrderById.has(localTask.Id)) localTask.SortOrder = remoteOrderById.get(localTask.Id);
  }

  return remoteOrderModifiedAt;
}

// Port of SavedViewSyncMerge.Merge (Services/SavedViewSyncMerge.cs) - much simpler than the task
// merge above since views have no ModifiedAt/collaborative-edit concept: union DeletedSavedViewIds
// from both sides, then union SavedViews by Id (remote inserted first, local second, so local wins
// a same-Id collision - an arbitrary but deterministic tiebreak, same as the C# side) minus
// anything in the merged deleted-id set. Mutates localState.SavedViews/DeletedSavedViewIds in place,
// matching mergeRemoteState's own in-place-mutation contract.
export function mergeSavedViews(localState, remoteState) {
  const deletedIds = new Set([
    ...(localState.DeletedSavedViewIds ?? []),
    ...(remoteState.DeletedSavedViewIds ?? []),
  ]);

  const merged = new Map();
  for (const v of remoteState.SavedViews ?? []) {
    if (!deletedIds.has(v.Id)) merged.set(v.Id, v);
  }
  for (const v of localState.SavedViews ?? []) {
    if (!deletedIds.has(v.Id)) merged.set(v.Id, v);
  }

  localState.SavedViews = [...merged.values()];
  localState.DeletedSavedViewIds = [...deletedIds];
}
