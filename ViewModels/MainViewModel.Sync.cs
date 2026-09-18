using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using TodoApp.Models;
using TodoApp.Services;
using TodoApp;

namespace TodoApp.ViewModels;

// The UI-bound half of Google Drive sync: kicking a pass off, applying TaskSyncMerge's plan to
// the bound collections, and the tombstones deletion leaves behind for the merge.
public partial class MainViewModel
{
    // Attaches to a file the user picked from their existing Google Drive files. Returns false
    // only when the user explicitly backed out of picking a destination for a genuinely separate
    // file - the caller uses that to know a sync shouldn't run right afterward.
    private async Task<bool> AttachExistingGoogleDriveFileAsync(string remoteFileId, string remoteFileName)
    {
        await FlushPendingSaveAsync();

        var currentFileName = Path.GetFileName(_currentFilePath);
        if (string.Equals(currentFileName, remoteFileName, StringComparison.OrdinalIgnoreCase))
        {
            // The remote file you picked shares this device's current local filename - Tasky.tasky
            // is every install's default, so this is the common case (attaching a second device to
            // an existing synced file), not a rare collision. Treating it as "download a separate
            // copy, ask where to put it" would either overwrite whatever's already open here or -
            // if you picked a different destination - silently abandon it, since the app would
            // switch to the new file and never look at the old one again. Just linking the ID here
            // and letting the caller's normal sync pass run right after (as it always does) merges
            // the remote content into what's already open instead, the same way any other sync
            // would - no separate download-and-switch step needed since it's already the open file.
            var fileKey = currentFileName.ToLowerInvariant();
            _sync.MarkLegacyAttachmentsOwnerIfUnset(fileKey);
            _settings.GoogleDriveFileIdsByFile[fileKey] = remoteFileId;
            _settingsStore.Save(_settings);
            return true;
        }

        // Different filename - this really is a separate file, so download it to its own local
        // path and make it the active file.
        var defaultDir = Path.GetDirectoryName(TodoStore.GetDefaultDataFilePath())
            ?? TaskyPaths.DocumentsRoot;
        Directory.CreateDirectory(defaultDir);
        var targetPath = Path.Combine(defaultDir, remoteFileName);

        // Don't silently overwrite an unrelated local file that happens to share this name -
        // let the user pick a different destination instead. A directory of the same name is
        // just as much a collision as a file (Directory.CreateDirectory further up won't create
        // "Documents\Tasky\Tasky.tasky" as a folder itself, but nothing rules out one already
        // existing there from outside the app).
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            // The Save As dialog that follows is easy to misread as part of a normal download -
            // explain up front why it's asking, since this only happens when the file being
            // attached has nothing to do with whatever already has this name locally.
            ThemedMessageBox.Show(
                $"A local file named \"{remoteFileName}\" already exists that isn't related to the " +
                "file you just selected. Choose a different name or location to save the downloaded " +
                "copy so it doesn't overwrite that file.",
                "Naming Conflict", MessageBoxButton.OK, MessageBoxImage.Information);

            var dialog = new SaveFileDialog
            {
                Title = "Save Downloaded Tasky File As",
                Filter = "Tasky files (*.tasky)|*.tasky",
                FileName = remoteFileName,
                InitialDirectory = defaultDir
            };
            if (dialog.ShowDialog() != true) return false;
            targetPath = dialog.FileName;
        }

        var targetFileKey = Path.GetFileName(targetPath).ToLowerInvariant();
        // This device might be attaching to the one file that already has real attachments
        // sitting in the shared flat Drive layout (e.g. a fresh install picking up a
        // long-established file) - if it has no legacy owner of its own yet, assume this could
        // be it, so the download below actually finds them instead of coming up empty.
        _sync.MarkLegacyAttachmentsOwnerIfUnset(targetFileKey);

        await _googleDrive.DownloadFileAsync(remoteFileId, targetPath, downloadAttachments: true, _settings, _settingsStore);
        LoadFile(targetPath);

        _settings.GoogleDriveFileIdsByFile[targetFileKey] = remoteFileId;
        _settingsStore.Save(_settings);
        return true;
    }

    public async Task PerformGoogleDriveSyncAsync(bool isSilentOnExit = false)
    {
        // Counted, not a plain bool: SyncCoordinator turns an overlapping call away immediately
        // (only one pass runs at a time), and that call's `finally` used to set IsSyncing = false
        // while the real pass was still running - hiding its progress bar mid-sync.
        if (_syncCallsInFlight++ == 0)
        {
            IsSyncing = true;
            SyncProgressPercent = 0;
        }
        var fileSessionAtStart = _fileSessionId;
        try
        {
            await _sync.PerformSyncAsync(
                _state,
                _currentFilePath,
                FlushPendingSaveAsync,
                remoteState =>
                {
                    var result = MergeRemoteState(remoteState);
                    _reminders.Reschedule();
                    RefreshTags();
                    RefreshViews();
                    FilteredTasksView.Refresh();
                    return result;
                },
                status => SaveStatusText = status,
                () => OpenSettingsWindow(SettingsSection.GoogleDrive),
                isSilentOnExit,
                percent => SyncProgressPercent = percent,
                () => fileSessionAtStart == _fileSessionId);

            // Deliberately after PerformSyncAsync fully returns, not inside its merge callback
            // above - AutoEmptyTrashIfNeeded's OnTaskChanged() calls Save(), which writes the same
            // local file SyncCoordinator is still mid-writing/uploading at that point (and would
            // clobber SaveStatusText's "Syncing..." with "Saving..." while that's still visible).
            // Any pruning found here rides along on the next sync instead, same as any other edit.
            AutoEmptyTrashIfNeeded();
        }
        finally
        {
            // Left visible at whatever percent it reached (100 on success) for a beat rather than
            // snapped back to 0 - IsSyncing=false hides the bar entirely via its Visibility binding,
            // so the exact leftover percent doesn't matter once that happens.
            if (--_syncCallsInFlight == 0) IsSyncing = false;
        }

        OnPropertyChanged(nameof(GoogleDriveStatusTooltip));
        OnPropertyChanged(nameof(IsGoogleDriveConnected));
    }

    // Permanent delete has no undo path (unlike Move to Trash), so a tombstone recorded here
    // never needs to be retracted. Without this, Google Drive's per-task merge would have no way
    // to tell "a device deleted this task" apart from "a device just hasn't pulled this task
    // down yet" - both look identical (missing from that device's list) without a record of which
    // task IDs were actually deleted and when.
    //
    // A task can legitimately be tombstoned more than once in its lifetime - delete, then a later
    // edit on another device revives it (an intentional part of the merge - see MergeRemoteState),
    // then it gets deleted again. Update the existing tombstone's timestamp instead of appending a
    // second one for the same TaskId: MergeRemoteState builds a Dictionary keyed by TaskId from
    // this list, which throws on a duplicate key - a second entry wouldn't just pick the "wrong"
    // timestamp, it would crash the sync outright, and keep crashing on every retry.
    private void RecordTaskDeletionTombstone(TaskItem task)
    {
        var existing = _state.DeletedTasks.FirstOrDefault(r => r.TaskId == task.Id);
        if (existing is not null)
            existing.Timestamp = DateTime.UtcNow;
        else
            _state.DeletedTasks.Add(new TaskSyncRecord { TaskId = task.Id, Timestamp = DateTime.UtcNow });
    }

    // Belt-and-suspenders for data written before RecordTaskDeletionTombstone deduplicated on
    // write (or any other source of a malformed file, e.g. hand-edited) - MergeRemoteState builds
    // a Dictionary keyed by TaskId from this list, which throws on a duplicate key, so a file
    // that already has one has to be cleaned up before it ever reaches that point. Keeps the
    // latest timestamp per TaskId, applied to both local (on load) and remote (right after
    // download) so neither side can be the one that crashes the merge.
    //
    // The decision logic itself (which tasks to add/update/remove, tombstone union) lives in
    // TaskSyncMerge.ComputeMergePlan - a pure function with no dependency on AllTasks or WPF
    // binding, so it's unit-testable without constructing a MainViewModel. This method is just
    // the thin, UI-bound half: apply that plan to AllTasks/AttachTask/DetachTask/SelectedTask.
    private (int Added, int Updated, int Removed, int Conflicted) MergeRemoteState(AppState remoteState)
    {
        var lastSyncTimeUtc = _settings.LastGoogleDriveSyncTime?.ToUniversalTime();
        var localBaselineUtc = _settings.LastGoogleDriveSyncLocalBaselineUtc is { } baseline
            ? baseline.ToUniversalTime()
            : lastSyncTimeUtc;
        var plan = TaskSyncMerge.ComputeMergePlan(_state.Tasks, remoteState.Tasks, _state.DeletedTasks, remoteState.DeletedTasks,
            lastSyncTimeUtc, localBaselineUtc);

        foreach (var remoteTask in plan.TasksToAdd)
        {
            AllTasks.Add(remoteTask);
            AttachTask(remoteTask);
        }

        foreach (var localTask in plan.TasksToRemove)
        {
            DetachTask(localTask);
            AllTasks.Remove(localTask);
            if (SelectedTask == localTask) SelectedTask = null;
        }

        // Detached first, since TaskItem's property setters trigger Task_PropertyChanged while
        // attached, which would stamp ModifiedAt to "now" (clobbering the timestamp being restored
        // here) and can spawn a recurring-task occurrence or push an undo entry - none of which
        // belong in a sync merge.
        foreach (var (localTask, remoteTask) in plan.TasksToUpdate)
        {
            DetachTask(localTask);
            TaskSyncMerge.ApplyTaskFields(localTask, remoteTask);
            AttachTask(localTask);
        }

        // ROADMAP.md #119: surfaced instead of the losing edit just disappearing - see
        // TaskSyncMerge.CreateConflictedCopy. Added like any other new task (undo doesn't apply to
        // a sync merge, same as TasksToAdd above).
        foreach (var conflictedCopy in plan.ConflictedCopiesToAdd)
        {
            AllTasks.Add(conflictedCopy);
            AttachTask(conflictedCopy);
        }

        _state.DeletedTasks.AddRange(plan.TombstonesToAdd);

        // After the field updates above, so remote's arrangement wins over any SortOrder
        // ApplyTaskFields just copied. Safe to run on attached tasks: Task_PropertyChanged returns
        // early for SortOrder, so this can't stamp ModifiedAt on the whole list.
        _state.TasksOrderModifiedAt = TaskSyncMerge.MergeTaskOrder(
            AllTasks, remoteState.Tasks, _state.TasksOrderModifiedAt, remoteState.TasksOrderModifiedAt);

        var (mergedViews, mergedDeletedViewIds) = SavedViewSyncMerge.Merge(
            _state.SavedViews, remoteState.SavedViews, _state.DeletedSavedViewIds, remoteState.DeletedSavedViewIds);
        _state.SavedViews = mergedViews;
        _state.DeletedSavedViewIds = mergedDeletedViewIds;

        return (plan.TasksToAdd.Count, plan.TasksToUpdate.Count, plan.TasksToRemove.Count, plan.ConflictedCopiesToAdd.Count);
    }

    private void ScheduleGoogleDriveAutoSync()
    {
        if (_settings.IsGoogleDriveEnabled && _googleDrive.IsAuthenticated)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _autoSyncTimer.Stop();
                _autoSyncTimer.Start();
            });
        }
    }
}
