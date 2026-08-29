// Port of MainViewModel.cs's per-task 3-way merge (MergeRemoteState / ApplyTaskFields /
// DeduplicateTombstones), kept behaviorally identical so a file synced by the web app merges the
// same way a desktop client merging that same file would. See the C# comments for the full
// rationale; kept brief here to avoid drifting out of sync with the original as comments.
import { parseDotNetDate, newGuid, nowDotNet } from './model.js?v=83';

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
    if (parseDotNetDate(t.Timestamp).getTime() < cutoff) continue;
    const existing = byId.get(t.TaskId);
    if (!existing || parseDotNetDate(t.Timestamp) > parseDotNetDate(existing.Timestamp)) {
      byId.set(t.TaskId, t);
    }
  }
  return [...byId.values()];
}

function applyTaskFields(target, source) {
  target.Text = source.Text;
  target.IsDone = source.IsDone;
  target.IsClosed = source.IsClosed;
  target.IsPinned = source.IsPinned;
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
 * shape. lastSyncTime (a Date, or null/undefined if never synced before) is what distinguishes a
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

  return { added, updated, removed, conflicted };
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
