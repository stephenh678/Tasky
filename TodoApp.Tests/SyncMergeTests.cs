using System;
using System.Collections.Generic;
using TodoApp.Models;
using TodoApp.Services;

namespace TodoApp.Tests;

public class TombstoneDeduplicationTests
{
    [Fact]
    public void NoDuplicates_ReturnsAllRecordsUnchanged()
    {
        var a = new TaskSyncRecord { TaskId = Guid.NewGuid(), Timestamp = DateTime.Now };
        var b = new TaskSyncRecord { TaskId = Guid.NewGuid(), Timestamp = DateTime.Now };

        var result = TaskSyncMerge.DeduplicateTombstones(new List<TaskSyncRecord> { a, b });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DuplicateTaskId_KeepsOnlyTheLatestTimestamp()
    {
        var id = Guid.NewGuid();
        var older = new TaskSyncRecord { TaskId = id, Timestamp = new DateTime(2026, 1, 1) };
        var newer = new TaskSyncRecord { TaskId = id, Timestamp = new DateTime(2026, 6, 1) };

        var result = TaskSyncMerge.DeduplicateTombstones(new List<TaskSyncRecord> { older, newer });

        Assert.Single(result);
        Assert.Equal(newer.Timestamp, result[0].Timestamp);
    }

    [Fact]
    public void DuplicateTaskId_OrderOfInputDoesNotMatter()
    {
        var id = Guid.NewGuid();
        var older = new TaskSyncRecord { TaskId = id, Timestamp = new DateTime(2026, 1, 1) };
        var newer = new TaskSyncRecord { TaskId = id, Timestamp = new DateTime(2026, 6, 1) };

        var result = TaskSyncMerge.DeduplicateTombstones(new List<TaskSyncRecord> { newer, older });

        Assert.Single(result);
        Assert.Equal(newer.Timestamp, result[0].Timestamp);
    }

    [Fact]
    public void EmptyList_ReturnsEmptyList()
    {
        var result = TaskSyncMerge.DeduplicateTombstones(new List<TaskSyncRecord>());
        Assert.Empty(result);
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
        Assert.Equal(new DateTime(2026, 6, 1), target.ModifiedAt);
        Assert.Equal(new[] { "finance" }, target.Tags);
        Assert.Single(target.Body);
    }
}
