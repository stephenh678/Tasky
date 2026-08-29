using System;
using System.Collections.Generic;
using TodoApp.Models;
using TodoApp.Services;

namespace TodoApp.Tests;

public class TombstoneDeduplicationTests
{
    // Fixed reference "now" (not DateTime.UtcNow) so every test's fixture timestamps sit at a
    // known, deterministic distance from the 90-day retention cutoff regardless of when the suite
    // actually runs - the dedup-focused tests below all use timestamps well inside the window, so
    // retention (ROADMAP.md #140) doesn't interfere with what they're actually asserting.
    private static readonly DateTime Now = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoDuplicates_ReturnsAllRecordsUnchanged()
    {
        var a = new TaskSyncRecord { TaskId = Guid.NewGuid(), Timestamp = Now.AddDays(-1) };
        var b = new TaskSyncRecord { TaskId = Guid.NewGuid(), Timestamp = Now.AddDays(-1) };

        var result = TaskSyncMerge.DeduplicateTombstones(new List<TaskSyncRecord> { a, b }, Now);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DuplicateTaskId_KeepsOnlyTheLatestTimestamp()
    {
        var id = Guid.NewGuid();
        var older = new TaskSyncRecord { TaskId = id, Timestamp = Now.AddDays(-30) };
        var newer = new TaskSyncRecord { TaskId = id, Timestamp = Now.AddDays(-1) };

        var result = TaskSyncMerge.DeduplicateTombstones(new List<TaskSyncRecord> { older, newer }, Now);

        Assert.Single(result);
        Assert.Equal(newer.Timestamp, result[0].Timestamp);
    }

    [Fact]
    public void DuplicateTaskId_OrderOfInputDoesNotMatter()
    {
        var id = Guid.NewGuid();
        var older = new TaskSyncRecord { TaskId = id, Timestamp = Now.AddDays(-30) };
        var newer = new TaskSyncRecord { TaskId = id, Timestamp = Now.AddDays(-1) };

        var result = TaskSyncMerge.DeduplicateTombstones(new List<TaskSyncRecord> { newer, older }, Now);

        Assert.Single(result);
        Assert.Equal(newer.Timestamp, result[0].Timestamp);
    }

    [Fact]
    public void EmptyList_ReturnsEmptyList()
    {
        var result = TaskSyncMerge.DeduplicateTombstones(new List<TaskSyncRecord>(), Now);
        Assert.Empty(result);
    }

    // ROADMAP.md #140: tombstones older than the 90-day retention window are dropped entirely,
    // not just deduplicated - a device that's been offline longer than that resurrecting an
    // ancient deletion instead of finding a tombstone is the accepted tradeoff (see the
    // DeduplicateTombstones doc comment).
    [Fact]
    public void TombstoneOlderThanRetentionWindow_IsDropped()
    {
        var old = new TaskSyncRecord { TaskId = Guid.NewGuid(), Timestamp = Now.AddDays(-91) };

        var result = TaskSyncMerge.DeduplicateTombstones(new List<TaskSyncRecord> { old }, Now);

        Assert.Empty(result);
    }

    [Fact]
    public void TombstoneJustInsideRetentionWindow_IsKept()
    {
        var recent = new TaskSyncRecord { TaskId = Guid.NewGuid(), Timestamp = Now.AddDays(-89) };

        var result = TaskSyncMerge.DeduplicateTombstones(new List<TaskSyncRecord> { recent }, Now);

        Assert.Single(result);
    }

    [Fact]
    public void MixOfOldAndRecentTombstones_OnlyRecentSurvive()
    {
        var old = new TaskSyncRecord { TaskId = Guid.NewGuid(), Timestamp = Now.AddDays(-200) };
        var recent = new TaskSyncRecord { TaskId = Guid.NewGuid(), Timestamp = Now.AddDays(-5) };

        var result = TaskSyncMerge.DeduplicateTombstones(new List<TaskSyncRecord> { old, recent }, Now);

        Assert.Single(result);
        Assert.Equal(recent.TaskId, result[0].TaskId);
    }

    [Fact]
    public void NoNowProvided_DefaultsToRealCurrentTime()
    {
        // No explicit `now` - exercises the DateTime.UtcNow default path used by production callers.
        var justNow = new TaskSyncRecord { TaskId = Guid.NewGuid(), Timestamp = DateTime.UtcNow };

        var result = TaskSyncMerge.DeduplicateTombstones(new List<TaskSyncRecord> { justNow });

        Assert.Single(result);
    }
}

// Covers TaskSyncMerge.ComputeMergePlan - the per-task Google Drive conflict resolution that
// replaced a whole-file "which copy is newer" guess (see the doc comment on ComputeMergePlan
// itself for why). This is the highest-risk logic in the sync path: a wrong decision here means
// silent data loss, either a task vanishing that shouldn't have, or a deletion failing to
// propagate and the task coming back from the dead.
public class ComputeMergePlanTests
{
    private static TaskItem Task(DateTime modifiedAt, string text = "task") =>
        new() { Text = text, ModifiedAt = modifiedAt };

    private static readonly List<TaskSyncRecord> NoTombstones = new();

    [Fact]
    public void RemoteOnlyTask_IsAdded()
    {
        var remoteTask = Task(DateTime.Now);

        var plan = TaskSyncMerge.ComputeMergePlan(
            localTasks: new List<TaskItem>(),
            remoteTasks: new List<TaskItem> { remoteTask },
            localTombstones: NoTombstones,
            remoteTombstones: NoTombstones);

        Assert.Single(plan.TasksToAdd);
        Assert.Same(remoteTask, plan.TasksToAdd[0]);
        Assert.Empty(plan.TasksToUpdate);
        Assert.Empty(plan.TasksToRemove);
    }

    [Fact]
    public void RemoteOnlyTask_LocallyDeletedBeforeRemoteEdit_IsNotResurrected()
    {
        var deletedAt = new DateTime(2026, 1, 1);
        var remoteTask = Task(modifiedAt: deletedAt); // remote hasn't learned about the deletion yet
        remoteTask.Id = Guid.NewGuid();
        var tombstone = new TaskSyncRecord { TaskId = remoteTask.Id, Timestamp = deletedAt.AddDays(1) };

        var plan = TaskSyncMerge.ComputeMergePlan(
            localTasks: new List<TaskItem>(),
            remoteTasks: new List<TaskItem> { remoteTask },
            localTombstones: new List<TaskSyncRecord> { tombstone },
            remoteTombstones: NoTombstones);

        Assert.Empty(plan.TasksToAdd);
    }

    [Fact]
    public void RemoteOnlyTask_EditedAfterLocalDeletion_IsAddedBack()
    {
        var deletedAt = new DateTime(2026, 1, 1);
        var remoteTask = Task(modifiedAt: deletedAt.AddDays(1)); // edited on remote after this device deleted it
        var tombstone = new TaskSyncRecord { TaskId = remoteTask.Id, Timestamp = deletedAt };

        var plan = TaskSyncMerge.ComputeMergePlan(
            localTasks: new List<TaskItem>(),
            remoteTasks: new List<TaskItem> { remoteTask },
            localTombstones: new List<TaskSyncRecord> { tombstone },
            remoteTombstones: NoTombstones);

        Assert.Single(plan.TasksToAdd);
    }

    [Fact]
    public void LocalOnlyTask_IsLeftAloneToUploadNormally()
    {
        var localTask = Task(DateTime.Now);

        var plan = TaskSyncMerge.ComputeMergePlan(
            localTasks: new List<TaskItem> { localTask },
            remoteTasks: new List<TaskItem>(),
            localTombstones: NoTombstones,
            remoteTombstones: NoTombstones);

        Assert.Empty(plan.TasksToAdd);
        Assert.Empty(plan.TasksToRemove);
        Assert.Empty(plan.TasksToUpdate);
    }

    [Fact]
    public void LocalOnlyTask_DeletedRemotelyAndUntouchedSince_IsRemoved()
    {
        var deletedAt = new DateTime(2026, 1, 1);
        var localTask = Task(modifiedAt: deletedAt.AddDays(-1)); // not touched since the remote deletion
        var tombstone = new TaskSyncRecord { TaskId = localTask.Id, Timestamp = deletedAt };

        var plan = TaskSyncMerge.ComputeMergePlan(
            localTasks: new List<TaskItem> { localTask },
            remoteTasks: new List<TaskItem>(),
            localTombstones: NoTombstones,
            remoteTombstones: new List<TaskSyncRecord> { tombstone });

        Assert.Single(plan.TasksToRemove);
        Assert.Same(localTask, plan.TasksToRemove[0]);
    }

    [Fact]
    public void LocalOnlyTask_EditedAfterRemoteDeletion_SurvivesRatherThanBeingResurrectedAsDeleted()
    {
        var deletedAt = new DateTime(2026, 1, 1);
        var localTask = Task(modifiedAt: deletedAt.AddDays(1)); // edited locally after the remote deletion
        var tombstone = new TaskSyncRecord { TaskId = localTask.Id, Timestamp = deletedAt };

        var plan = TaskSyncMerge.ComputeMergePlan(
            localTasks: new List<TaskItem> { localTask },
            remoteTasks: new List<TaskItem>(),
            localTombstones: NoTombstones,
            remoteTombstones: new List<TaskSyncRecord> { tombstone });

        Assert.Empty(plan.TasksToRemove);
    }

    [Fact]
    public void TaskOnBothSides_NewerRemoteEdit_IsQueuedAsUpdate()
    {
        var id = Guid.NewGuid();
        var localTask = new TaskItem { Id = id, Text = "old", ModifiedAt = new DateTime(2026, 1, 1) };
        var remoteTask = new TaskItem { Id = id, Text = "new", ModifiedAt = new DateTime(2026, 6, 1) };

        var plan = TaskSyncMerge.ComputeMergePlan(
            localTasks: new List<TaskItem> { localTask },
            remoteTasks: new List<TaskItem> { remoteTask },
            localTombstones: NoTombstones,
            remoteTombstones: NoTombstones);

        Assert.Single(plan.TasksToUpdate);
        Assert.Same(localTask, plan.TasksToUpdate[0].Local);
        Assert.Same(remoteTask, plan.TasksToUpdate[0].Remote);
    }

    [Fact]
    public void TaskOnBothSides_NewerLocalEdit_IsNotOverwritten()
    {
        var id = Guid.NewGuid();
        var localTask = new TaskItem { Id = id, Text = "newer", ModifiedAt = new DateTime(2026, 6, 1) };
        var remoteTask = new TaskItem { Id = id, Text = "older", ModifiedAt = new DateTime(2026, 1, 1) };

        var plan = TaskSyncMerge.ComputeMergePlan(
            localTasks: new List<TaskItem> { localTask },
            remoteTasks: new List<TaskItem> { remoteTask },
            localTombstones: NoTombstones,
            remoteTombstones: NoTombstones);

        Assert.Empty(plan.TasksToUpdate);
    }

    [Fact]
    public void TaskOnBothSides_IdenticalTimestamp_LocalWins()
    {
        var id = Guid.NewGuid();
        var sameTime = new DateTime(2026, 3, 1);
        var localTask = new TaskItem { Id = id, ModifiedAt = sameTime };
        var remoteTask = new TaskItem { Id = id, ModifiedAt = sameTime };

        var plan = TaskSyncMerge.ComputeMergePlan(
            localTasks: new List<TaskItem> { localTask },
            remoteTasks: new List<TaskItem> { remoteTask },
            localTombstones: NoTombstones,
            remoteTombstones: NoTombstones);

        Assert.Empty(plan.TasksToUpdate); // ties don't count as "remote is newer"
    }

    [Fact]
    public void RemoteTombstones_NotAlreadyKnownLocally_AreUnionedIn()
    {
        var remoteTombstone = new TaskSyncRecord { TaskId = Guid.NewGuid(), Timestamp = DateTime.Now };

        var plan = TaskSyncMerge.ComputeMergePlan(
            localTasks: new List<TaskItem>(),
            remoteTasks: new List<TaskItem>(),
            localTombstones: NoTombstones,
            remoteTombstones: new List<TaskSyncRecord> { remoteTombstone });

        Assert.Single(plan.TombstonesToAdd);
        Assert.Same(remoteTombstone, plan.TombstonesToAdd[0]);
    }

    [Fact]
    public void RemoteTombstones_AlreadyKnownLocally_AreNotDuplicated()
    {
        var id = Guid.NewGuid();
        var localTombstone = new TaskSyncRecord { TaskId = id, Timestamp = DateTime.Now };
        var remoteTombstone = new TaskSyncRecord { TaskId = id, Timestamp = DateTime.Now.AddDays(-1) };

        var plan = TaskSyncMerge.ComputeMergePlan(
            localTasks: new List<TaskItem>(),
            remoteTasks: new List<TaskItem>(),
            localTombstones: new List<TaskSyncRecord> { localTombstone },
            remoteTombstones: new List<TaskSyncRecord> { remoteTombstone });

        Assert.Empty(plan.TombstonesToAdd);
    }

    // ROADMAP.md #119: when both sides edited a task since they last agreed, the loser used to
    // just vanish. Now it's kept as a separate "(conflicted copy)" task.
    [Fact]
    public void TaskOnBothSides_BothEditedSinceLastSync_LosingEditKeptAsConflictedCopy()
    {
        var id = Guid.NewGuid();
        var lastSync = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var localTask = new TaskItem { Id = id, Text = "local edit", ModifiedAt = lastSync.AddHours(1) };
        var remoteTask = new TaskItem { Id = id, Text = "remote edit", ModifiedAt = lastSync.AddHours(2) };

        var plan = TaskSyncMerge.ComputeMergePlan(
            localTasks: new List<TaskItem> { localTask },
            remoteTasks: new List<TaskItem> { remoteTask },
            localTombstones: NoTombstones,
            remoteTombstones: NoTombstones,
            lastSyncTimeUtc: lastSync);

        Assert.Single(plan.TasksToUpdate); // remote still wins the original task ID
        Assert.Single(plan.ConflictedCopiesToAdd);
        var copy = plan.ConflictedCopiesToAdd[0];
        Assert.NotEqual(id, copy.Id); // distinct identity, doesn't collide with the winner on the next sync
        Assert.Equal("local edit (conflicted copy)", copy.Text);
    }

    [Fact]
    public void TaskOnBothSides_LocalUnchangedSinceLastSync_NoConflictedCopy()
    {
        var id = Guid.NewGuid();
        var lastSync = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        // Local hasn't been touched since the last sync - remote's newer edit is just new
        // information, not a competing edit, even though it's technically "both present."
        var localTask = new TaskItem { Id = id, Text = "old", ModifiedAt = lastSync.AddHours(-1) };
        var remoteTask = new TaskItem { Id = id, Text = "new", ModifiedAt = lastSync.AddHours(1) };

        var plan = TaskSyncMerge.ComputeMergePlan(
            localTasks: new List<TaskItem> { localTask },
            remoteTasks: new List<TaskItem> { remoteTask },
            localTombstones: NoTombstones,
            remoteTombstones: NoTombstones,
            lastSyncTimeUtc: lastSync);

        Assert.Single(plan.TasksToUpdate);
        Assert.Empty(plan.ConflictedCopiesToAdd);
    }

    [Fact]
    public void TaskOnBothSides_NoLastSyncTime_NoConflictedCopy()
    {
        // Never synced before (lastSyncTimeUtc omitted/null) - nothing to compare against yet.
        var id = Guid.NewGuid();
        var localTask = new TaskItem { Id = id, Text = "old", ModifiedAt = new DateTime(2026, 1, 1) };
        var remoteTask = new TaskItem { Id = id, Text = "new", ModifiedAt = new DateTime(2026, 6, 1) };

        var plan = TaskSyncMerge.ComputeMergePlan(
            localTasks: new List<TaskItem> { localTask },
            remoteTasks: new List<TaskItem> { remoteTask },
            localTombstones: NoTombstones,
            remoteTombstones: NoTombstones);

        Assert.Empty(plan.ConflictedCopiesToAdd);
    }

    [Fact]
    public void ApplyTaskFields_CopiesEveryMergeableFieldFromSourceOntoTarget()
    {
        var target = new TaskItem { Text = "old", IsDone = false, Tags = { "keep-me-out" } };
        var source = new TaskItem
        {
            Text = "new text",
            IsDone = true,
            IsClosed = true,
            IsPinned = true,
            DueDate = new DateTime(2026, 12, 25),
            Priority = TaskPriority.High,
            Recurrence = RecurrenceRule.Weekly,
            RecurrenceInterval = 3,
            ModifiedAt = new DateTime(2026, 6, 1),
        };
        source.Tags.Add("finance");
        source.Body.Add(new NoteBlock { Type = NoteBlockType.Text, Text = "note" });

        TaskSyncMerge.ApplyTaskFields(target, source);

        Assert.Equal("new text", target.Text);
        Assert.True(target.IsDone);
        Assert.True(target.IsClosed);
        Assert.True(target.IsPinned);
        Assert.Equal(new DateTime(2026, 12, 25), target.DueDate);
        Assert.Equal(TaskPriority.High, target.Priority);
        Assert.Equal(RecurrenceRule.Weekly, target.Recurrence);
        Assert.Equal(3, target.RecurrenceInterval);
        Assert.Equal(new DateTime(2026, 6, 1), target.ModifiedAt);
        Assert.Equal(new[] { "finance" }, target.Tags);
        Assert.Single(target.Body);
    }
}
