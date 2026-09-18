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

// Which .tasky file is open and getting it to and from disk: New/Open/Save As/backup
// commands, LoadFile, and the debounced autosave pipeline with its failure retry.
public partial class MainViewModel
{
    // New/Open/Save As/Restore Backup - commands that swap out which .tasky file is open or
    // touch the file on disk directly, rather than mutating in-memory task state.
    private void InitializeFileCommands()
    {
        NewFileCommand = new AsyncRelayCommand(async _ => await CreateNewLocalFileForSyncAsync());

        OpenFileCommand = new RelayCommand(_ =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open Tasky File",
                Filter = "Tasky files (*.tasky)|*.tasky|JSON files (*.json)|*.json|All files (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                LoadFile(dialog.FileName);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // LoadFile reads before it mutates, so the previously open file is still open.
                App.LogException(ex);
                ThemedMessageBox.Show($"Couldn't open this file:\n{ex.Message}\n\nYour current file is still open and unchanged.",
                    "Open Tasky File", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Renaming a synced .tasky file outside the app (Explorer) then reopening it here
            // looks, from the app's perspective, identical to opening a brand-new file - there's
            // no reliable way to tell "this is file X under a new name" from "this really is a
            // new file" by name alone. Rather than silently create a duplicate remote file on the
            // next sync, nudge toward the explicit fix (Choose File) - but only once this device
            // has actual sync history to plausibly be renaming *from*, so a first-time Drive user
            // opening an old file doesn't get an unexplained warning.
            if (_settings.IsGoogleDriveEnabled && _googleDrive.IsAuthenticated
                && _settings.GoogleDriveFileIdsByFile.Count > 0
                && !_settings.GoogleDriveFileIdsByFile.ContainsKey(Path.GetFileName(dialog.FileName).ToLowerInvariant()))
            {
                SaveStatusText = "This file isn't linked to Google Drive yet - syncing will create a new remote copy. If it's a renamed version of a file you already sync, use Google Drive → Choose File to link it instead.";
            }
        });

        SaveFileAsCommand = new AsyncRelayCommand(async _ =>
        {
            FlushPendingSave();
            var dialog = new SaveFileDialog
            {
                Title = "Save Tasky File As",
                Filter = "Tasky files (*.tasky)|*.tasky",
                FileName = Path.GetFileNameWithoutExtension(_currentFilePath) + ".tasky"
            };
            if (dialog.ShowDialog() != true) return;

            // Write first, switch second: repointing _currentFilePath before a save that then
            // failed (read-only folder, path too long) left autosave aimed at a file that doesn't
            // exist, with the media resolver looking beside it.
            // ROADMAP.md #124: SaveAsync awaited directly (this handler is already off the sync
            // call stack once ShowDialog returns) instead of the blocking Save()/GetResult() bridge.
            var previousFilePath = _currentFilePath;
            try
            {
                CopyReferencedMedia(previousFilePath, dialog.FileName);
                await _store.SaveAsync(_state, dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                App.LogException(ex);
                ThemedMessageBox.Show($"Couldn't save to that location:\n{ex.Message}", "Save Tasky File As",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _fileSessionId++;
            _currentFilePath = dialog.FileName;
            MediaPathResolver.SetDataFilePath(_currentFilePath);
            OnPropertyChanged(nameof(WindowTitle));

            _settings.LastFilePath = _currentFilePath;
            _settingsStore.Save(_settings);
        });

        RestoreBackupCommand = new AsyncRelayCommand(async _ =>
        {
            _isRestoringBackup = true;
            try
            {
                // Unlike the other FlushPendingSave() call sites, this one genuinely needs the disk
                // write to have landed before RestoreBackup overwrites the file out from under it -
                // await the real completion instead of just firing it off.
                await FlushPendingSaveAsync();
                var backups = _store.ListBackups(_currentFilePath);
                if (backups.Count == 0)
                {
                    ThemedMessageBox.Show("No backups found for this file yet.", "Restore from Backup",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var picker = new RestoreBackupWindow(backups) { Owner = Application.Current.MainWindow };
                if (picker.ShowDialog() != true || picker.SelectedBackup is null) return;

                var confirm = ThemedMessageBox.Show(
                    $"Restore the backup from {picker.SelectedBackup.Timestamp:MMM d, yyyy 'at' h:mm:ss tt}?\n\n" +
                    "Your current file will be backed up first, so this can be undone by restoring again.",
                    "Restore from Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                _store.RestoreBackup(picker.SelectedBackup.FilePath, _currentFilePath);
                LoadFile(_currentFilePath);
                MarkAllTasksRestoredAndSave();
            }
            finally
            {
                _isRestoringBackup = false;
            }
        }, _ => !_isRestoringBackup);

        // Export/Import Full Backup - a portable .zip of the data file plus every attachment it
        // references, for moving everything to a new machine or just keeping an offline copy.
        // Distinct from Save As (data only, no attachments) and Restore from Backup (data only,
        // and only ever from this same machine's own Backups\ history).
        ExportBackupCommand = new AsyncRelayCommand(async _ =>
        {
            await FlushPendingSaveAsync();
            var dialog = new SaveFileDialog
            {
                Title = "Export Full Backup",
                Filter = "Zip archive (*.zip)|*.zip",
                FileName = $"Tasky Backup {DateTime.Now:yyyy-MM-dd}.zip"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var (included, missing) = BackupService.Export(_currentFilePath, AllTasks, dialog.FileName);
                var message = $"Exported {AllTasks.Count} task(s) and {included} attachment(s) to:\n{dialog.FileName}";
                if (missing > 0)
                    message += $"\n\n{missing} attachment(s) referenced by your tasks couldn't be found locally and were skipped.";
                ThemedMessageBox.Show(message, "Export Full Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                App.LogException(ex);
                ThemedMessageBox.Show($"Couldn't export: {ex.Message}", "Export Full Backup", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });

        ExportCalendarCommand = new RelayCommand(_ =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export Due Dates to Calendar",
                Filter = "iCalendar file (*.ics)|*.ics",
                FileName = $"Tasky Due Dates {DateTime.Now:yyyy-MM-dd}.ics"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var count = ExportService.ExportToICalendar(AllTasks, dialog.FileName);
                var message = count == 0
                    ? "No open tasks have a due date set, so nothing was exported."
                    : $"Exported {count} due date(s) to:\n{dialog.FileName}\n\nImport this file into Google Calendar, Outlook, or Apple Calendar.";
                ThemedMessageBox.Show(message, "Export Due Dates to Calendar", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                App.LogException(ex);
                ThemedMessageBox.Show($"Couldn't export: {ex.Message}", "Export Due Dates to Calendar", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });

        // ROADMAP.md #135: whole-list export, alongside the existing per-note "Export Selected
        // Note..." (ExportNote_Click in MainWindow.xaml.cs). Doesn't need the live FlowDocument
        // that per-note export reads from, so - unlike that one - this can be a plain command
        // here instead of MainWindow.xaml.cs code-behind.
        ExportAllTasksCommand = new RelayCommand(_ =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "Export All Tasks",
                Filter = "Markdown Document (*.md)|*.md",
                FileName = $"Tasky Export {DateTime.Now:yyyy-MM-dd}.md"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                ExportService.ExportAllToMarkdown(AllTasks, dialog.FileName);
                ThemedMessageBox.Show($"Exported all tasks to:\n{dialog.FileName}", "Export All Tasks", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                App.LogException(ex);
                ThemedMessageBox.Show($"Couldn't export: {ex.Message}", "Export All Tasks", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });

        ImportBackupCommand = new AsyncRelayCommand(async _ =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Import Full Backup",
                Filter = "Zip archive (*.zip)|*.zip"
            };
            if (dialog.ShowDialog() != true) return;

            ExtractedBackupPackage package;
            try
            {
                package = BackupService.ExtractToTemp(dialog.FileName);
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show($"Couldn't read this backup:\n{ex.Message}", "Import Full Backup",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            using (package)
            {
                int backupTaskCount;
                try
                {
                    // AllTasks is the CURRENTLY open file's tasks (the ones about to be replaced), not
                    // the backup's - reading the extracted backup itself is the only way to show its
                    // real count here, same as how the Drive sync merge peeks at a downloaded remote
                    // file. Kept in this same try/catch since a corrupt backup can fail either step.
                    // ROADMAP.md #124: awaited directly instead of the blocking Load()/GetResult() bridge - safe here since ImportBackupCommand's handler is already async.
                    backupTaskCount = (await _store.LoadAsync(package.DataFilePath)).Tasks.Count;
                }
                catch (Exception ex)
                {
                    ThemedMessageBox.Show($"Couldn't read this backup:\n{ex.Message}", "Import Full Backup",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var confirm = ThemedMessageBox.Show(
                    $"This will replace your currently open task list with the backup's {backupTaskCount} " +
                    $"task(s) and restore its {package.AttachmentFiles.Count} attachment(s).\n\n" +
                    "Your current file will be backed up first, so this can be undone by restoring it from Restore from Backup.",
                    "Import Full Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                _isRestoringBackup = true;
                try
                {
                    await FlushPendingSaveAsync();
                    BackupService.RestoreAttachments(package.AttachmentFiles);
                    _store.RestoreBackup(package.DataFilePath, _currentFilePath);
                    LoadFile(_currentFilePath);
                    MarkAllTasksRestoredAndSave();

                    ThemedMessageBox.Show($"Imported {package.AttachmentFiles.Count} attachment(s) and restored your tasks.",
                        "Import Full Backup", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    App.LogException(ex);
                    ThemedMessageBox.Show($"Couldn't import: {ex.Message}", "Import Full Backup", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    _isRestoringBackup = false;
                }
            }
        }, _ => !_isRestoringBackup);

        ClearDebugLogCommand = new RelayCommand(_ =>
        {
            var confirm = ThemedMessageBox.Show(
                "Are you sure you want to clear the debug log file?\n\nExisting entries will be truncated and a fresh log will be started.",
                "Clear Debug Log", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            AppLogger.ClearLogFile();
            ThemedMessageBox.Show("Debug log file has been cleared.", "Debug Log", MessageBoxButton.OK, MessageBoxImage.Information);
        });

        OpenDebugLogCommand = new RelayCommand(_ =>
        {
            AppLogger.Info("MainViewModel", "User requested to open debug log file");
            var result = AppLogger.OpenLogFile(out var error);
            switch (result)
            {
                case AppLogger.OpenLogFileResult.NotCreatedYet:
                    ThemedMessageBox.Show($"Log file not created yet:\n{AppLogger.LogFilePath}", "Debug Log",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                case AppLogger.OpenLogFileResult.Failed:
                    ThemedMessageBox.Show($"Unable to open log file:\n{error}", "Debug Log",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        });

        GoogleDriveCommand = new RelayCommand(_ => OpenSettingsWindow(SettingsSection.GoogleDrive));

        SyncGoogleDriveNowCommand = new AsyncRelayCommand(async _ => await PerformGoogleDriveSyncAsync());

        SettingsCommand = new RelayCommand(_ => OpenSettingsWindow(SettingsSection.General));
    }

    // Same New File flow as NewFileCommand, exposed for the Google Drive "Choose File" picker so
    // choosing "Create New" there doesn't just silently reuse whatever file already happens to be
    // open - it's an explicit choice, same as picking an existing remote file is.
    private async Task<bool> CreateNewLocalFileForSyncAsync()
    {
        FlushPendingSave();
        var dialog = new SaveFileDialog
        {
            Title = "New Tasky File",
            Filter = "Tasky files (*.tasky)|*.tasky",
            FileName = "Tasky.tasky"
        };
        if (dialog.ShowDialog() != true) return false;

        // ROADMAP.md #124: awaited directly instead of the blocking Save()/GetResult() bridge.
        await _store.SaveAsync(new AppState(), dialog.FileName);
        LoadFile(dialog.FileName);
        return true;
    }

    // Attachments/InlineImages live next to the data file (MediaPathResolver.DirectoryFor), so a
    // Save As into a different folder has to bring along the files its tasks reference - otherwise
    // every image and file card in the new copy points at a folder that doesn't have them.
    private void CopyReferencedMedia(string fromDataFile, string toDataFile)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in AllTasks)
            ExtractTaskMediaFilenames(task, referenced);
        if (referenced.Count == 0) return;

        foreach (var dirName in new[] { "Attachments", "InlineImages" })
        {
            var fromDir = MediaPathResolver.DirectoryFor(fromDataFile, dirName);
            var toDir = MediaPathResolver.DirectoryFor(toDataFile, dirName);
            if (string.Equals(fromDir, toDir, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var fileName in referenced)
            {
                var source = Path.Combine(fromDir, fileName);
                var destination = Path.Combine(toDir, fileName);
                if (!File.Exists(source) || File.Exists(destination)) continue;
                try
                {
                    Directory.CreateDirectory(toDir);
                    File.Copy(source, destination);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("MainViewModel", $"Save As: couldn't copy '{source}' to '{destination}': {ex.Message}");
                }
            }
        }
    }

    // Determines which file to open on startup: the last file the user had open, otherwise the
    // default Documents location (a missing file there just means a fresh, blank AppState - see
    // TodoStore.Load). The one-time migration off the old fixed AppData location happened long
    // enough ago in this app's life that keeping it live was actively harmful: it meant a
    // deliberately-deleted default file would silently come back populated with whatever stale
    // data happened to still be sitting in that old AppData location, instead of actually
    // starting fresh.
    private string ResolveInitialFilePath()
    {
        if (_settings.LastFilePath is { } last && File.Exists(last))
            return last;

        return TodoStore.GetDefaultDataFilePath();
    }

    internal void LoadFile(string path, bool restoreSelection = false)
    {
        AppLogger.Info("MainViewModel", $"LoadFile: Loading file '{path}' (restoreSelection={restoreSelection})");
        FlushPendingSave();

        // Read the new file BEFORE touching anything. This used to clear AllTasks and repoint
        // _currentFilePath first, so a file that then failed to load (corrupt JSON, still locked
        // after the retries) left an EMPTY in-memory state aimed at that very file - and the next
        // autosave wrote the empty state over it. If this throws, nothing has changed: the
        // previously open file is still open and still intact. (Blocking Load, not LoadAsync - see
        // the note further down.)
        var loaded = _store.Load(path);

        // Any Drive sync still in flight was started for the previous file - see
        // SyncCoordinator.PerformSyncAsync's isFileSessionCurrent.
        _fileSessionId++;

        foreach (var task in AllTasks)
            DetachTask(task);
        AllTasks.Clear();
        _undoStack.Clear();
        _reminders.ClearNotified();
        OnPropertyChanged(nameof(UndoMenuLabel));

        _currentFilePath = path;
        MediaPathResolver.SetDataFilePath(path);

        // _state is never reassigned (see its declaration) - AllTasks and FilteredTasksView both
        // wrap _state.Tasks by reference, so opening a different file means repopulating that
        // same collection in place from a freshly-loaded AppState, not swapping _state itself out
        // for a new one (which would leave FilteredTasksView pointed at the old, now-orphaned
        // collection).
        //
        // Deliberately still the blocking Load(), not LoadAsync (ROADMAP.md #124's other call
        // sites - SaveFileAsCommand, CreateNewLocalFileForSync, ImportBackupCommand - now await the
        // async path). LoadFile itself is called from six places including the constructor's
        // synchronous startup path (line ~502), which can't await without either going fully
        // fire-and-forget there (a visible empty-window flash on launch) or a larger restructure -
        // same "high-blast-radius, left for a dedicated pass" call the #15 FileSessionManager
        // extraction made about this exact method.
        foreach (var task in loaded.Tasks)
        {
            AllTasks.Add(task);
            AttachTask(task);
        }

        // DeletedTasks isn't bound to any UI collection (unlike Tasks/AllTasks), so a plain
        // reassignment is safe here - but it still has to happen, or a tombstone written to disk
        // by a previous session stays invisible to Google Drive's merge (which only ever
        // consults the in-memory _state.DeletedTasks), letting a deleted task get silently
        // resurrected on the next sync.
        _state.DeletedTasks = TaskSyncMerge.DeduplicateTombstones(loaded.DeletedTasks);

        // Same reasoning for saved Views: without this they vanish from the sidebar on every
        // launch, the next save writes the empty in-memory list over what's on disk, and opening a
        // different file carries the previous file's views into it.
        _state.SavedViews = loaded.SavedViews ?? new();
        _state.DeletedSavedViewIds = loaded.DeletedSavedViewIds ?? new();

        AppLogger.Info("MainViewModel", $"LoadFile: Loaded {loaded.Tasks.Count} tasks into AllTasks");
        _state.TasksOrderModifiedAt = loaded.TasksOrderModifiedAt;
        // Pre-SortOrder data (and files written by a Tasky Web build older than the one that learned
        // to stamp SortOrder) arrive all-zero - lay down a sequential order so a first drag has
        // something to move within. Deliberately does NOT stamp TasksOrderModifiedAt: this is a
        // local backfill of an arbitrary order, not a user's arrangement, and letting it win a merge
        // would overwrite a real ordering made on another device.
        if (AllTasks.Count > 0 && AllTasks.All(t => t.SortOrder == 0))
        {
            for (int i = 0; i < AllTasks.Count; i++)
            {
                AllTasks[i].SortOrder = i;
            }
        }

        SelectedTask = null;
        SelectedSidebarItem = _allItem;
        AutoEmptyTrashIfNeeded();
        RefreshTags();
        RefreshViews();
        FilteredTasksView.Refresh();
        OnPropertyChanged(nameof(WindowTitle));

        _settings.LastFilePath = path;
        _settingsStore.Save(_settings);

        if (restoreSelection && _settings.LastSelectedTaskId is { } lastId && Guid.TryParse(lastId, out var guid))
        {
            var match = AllTasks.FirstOrDefault(t => t.Id == guid);
            if (match is not null) SelectedTask = match;
        }
    }

    // Shared by RestoreBackupCommand and ImportBackupCommand, called right after LoadFile reloads
    // a backup's tasks - they carry whatever ModifiedAt they had at backup time, almost always
    // older than what's since accumulated on remote. Left alone, the very next Drive sync's
    // last-write-wins merge (MergeRemoteState) would treat the restored copy as the stale side
    // and silently overwrite it right back with the pre-restore remote state, defeating the
    // restore the user just confirmed. Each task gets a distinct tick offset off the same restore
    // moment rather than one identical DateTime.Now for all of them, so "sort by Modified" doesn't
    // collapse into an arbitrary tie for every task until each is edited again.
    //
    // Known, deliberate tradeoff: this also makes a restored task win against a remote TOMBSTONE,
    // not just a remote edit - MergeRemoteState's local-only-task removal only fires when
    // localTask.ModifiedAt <= the tombstone's deletedAt, which can never be true once ModifiedAt
    // is bumped to "now". So if a task was deleted on another device sometime after this backup's
    // snapshot was taken but before this restore, restoring will resurrect it on the next sync.
    // Fixing that properly means teaching MergeRemoteState to tell "beat a stale edit" apart from
    // "beat a newer deletion" for a restored task - real surgery on the shared merge algorithm for
    // a narrow edge case (needs both an old backup restore AND a genuine cross-device delete of
    // that exact task in the gap between snapshot and restore). Left as-is on purpose rather than
    // risking that code for this. Don't "fix" this reactively without re-reading this comment.
    private void MarkAllTasksRestoredAndSave()
    {
        var restoredAt = DateTime.UtcNow;
        var offset = 0;
        foreach (var task in AllTasks)
            task.ModifiedAt = restoredAt.AddTicks(offset++);
        RequestDebouncedSave();
    }

    private void RequestDebouncedSave()
    {
        SaveStatusText = "Saving…";
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    private void CommitSave()
    {
        _saveDebounceTimer.Stop();
        OnTaskChanged();
    }

    // Call before anything that would otherwise lose the last few seconds of debounced typing:
    // switching tasks, switching files, or closing the app. This only guarantees the pending edit
    // has been HANDED OFF to a save (in-memory state is already current the instant CommitSave
    // runs) - it does not wait for that save to land on disk. That's fine for callers that only
    // care about in-memory state (e.g. switching the selected task); callers that need the disk
    // write itself to have finished (restoring a backup, closing the app) should use
    // FlushPendingSaveAsync instead.
    public void FlushPendingSave()
    {
        if (_saveDebounceTimer.IsEnabled)
            CommitSave();
    }

    public async Task FlushPendingSaveAsync()
    {
        if (_saveDebounceTimer.IsEnabled)
            CommitSave();
        await _pendingSaveTask;

        // The last attempt failed and its retry hasn't fired yet - a caller that needs the disk to
        // be current (closing the app, restoring a backup, syncing) gets that retry now rather
        // than proceeding on a stale file.
        if (LastSaveFailed)
        {
            _saveRetryTimer.Stop();
            Save();
            await _pendingSaveTask;
        }
    }

    /// <summary>True while the newest in-memory state has NOT made it to disk because the most
    /// recent save attempt failed. MainWindow checks this before letting the app exit.</summary>
    public bool LastSaveFailed { get; private set; }

    public string? LastSaveError { get; private set; }

    // The hot path: nearly every task edit (typing, checking a box, trashing, tagging...) routes
    // through here via OnTaskChanged. Runs off the UI thread instead of blocking on disk IO -
    // _pendingSaveTask is tracked so FlushPendingSaveAsync (restoring a backup, closing the app)
    // can still wait for a real completion when it actually matters.
    private void Save()
    {
        SaveStatusText = "Saving…";
        var generation = ++_saveGeneration;
        _pendingSaveTask = SaveAndReportAsync(generation);
    }

    // generation guards against two problems that come from Save() firing from multiple
    // overlapping call sites (the debounce timer AND every immediate property change): an older,
    // slower save finishing after a newer one started must not stamp "Saved" over a still-pending
    // edit's "Saving…" - and a failure must actually surface instead of leaving the status stuck
    // on "Saving…" forever with the edit silently unwritten.
    private async Task SaveAndReportAsync(int generation)
    {
        try
        {
            await _store.SaveAsync(_state, _currentFilePath);
            if (generation == _saveGeneration)
            {
                SaveStatusText = "Saved";
                LastSaveFailed = false;
                LastSaveError = null;
                _saveRetryTimer.Stop();
            }

            ScheduleGoogleDriveAutoSync();
        }
        catch (Exception ex)
        {
            App.LogException(ex);
            // Only the newest attempt decides the outcome - an older save failing after a newer
            // one already succeeded has nothing left to retry.
            if (generation == _saveGeneration)
            {
                LastSaveFailed = true;
                LastSaveError = ex.Message;
                SaveStatusText = "Couldn't save - retrying…";
                _saveRetryTimer.Stop();
                _saveRetryTimer.Start();
            }
        }
    }
}
