// Port of MainViewModel.cs's per-task 3-way merge (MergeRemoteState / ApplyTaskFields /
// DeduplicateTombstones), kept behaviorally identical so a file synced by the web app merges the
// same way a desktop client merging that same file would. See the C# comments for the full
// rationale; kept brief here to avoid drifting out of sync with the original as comments.
import { parseDotNetDate } from './model.js?v=14';

export function deduplicateTombstones(tombstones) {
  const byId = new Map();
  for (const t of tombstones) {
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
  target.Tags = [...source.Tags];
  target.Body = source.Body.map((b) => ({ ...b }));
  target.ModifiedAt = source.ModifiedAt;
}

/**
 * Merges remoteState into localState IN PLACE (mutates localState.Tasks / localState.DeletedTasks)
 * and returns { added, updated, removed } counts, matching MergeRemoteState's return shape.
 */
export function mergeRemoteState(localState, remoteState) {
  const localById = new Map(localState.Tasks.map((t) => [t.Id, t]));
  const remoteById = new Map(remoteState.Tasks.map((t) => [t.Id, t]));
  const localTombstones = new Map(localState.DeletedTasks.map((r) => [r.TaskId, parseDotNetDate(r.Timestamp)]));
  const remoteTombstones = new Map(remoteState.DeletedTasks.map((r) => [r.TaskId, parseDotNetDate(r.Timestamp)]));

  let added = 0;
  let updated = 0;
  let removed = 0;

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

  // Present on both sides: the newer edit wins.
  for (const [id, remoteTask] of remoteById) {
    const localTask = localById.get(id);
    if (!localTask) continue;
    if (parseDotNetDate(remoteTask.ModifiedAt) <= parseDotNetDate(localTask.ModifiedAt)) continue;
    applyTaskFields(localTask, remoteTask);
    updated++;
  }

  // Tombstones union both ways, so a third device merging later learns about every deletion
  // recorded anywhere.
  const mergedIds = new Set(localState.DeletedTasks.map((r) => r.TaskId));
  for (const remoteTombstone of remoteState.DeletedTasks) {
    if (!mergedIds.has(remoteTombstone.TaskId)) {
      mergedIds.add(remoteTombstone.TaskId);
      localState.DeletedTasks.push(remoteTombstone);
    }
  }

  return { added, updated, removed };
}
