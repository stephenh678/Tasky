using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using TodoApp;
using TodoApp.Models;

namespace TodoApp.Services;

/// <summary>
/// Owns Google Drive sync orchestration: remote file ID resolution (including the legacy
/// single-global-ID migration), download/merge/upload, and the in-progress guard against
/// overlapping runs. Extracted out of MainViewModel's PerformGoogleDriveSyncAsync (see
/// review_tasks.md's "Break up the MainViewModel god object" item).
///
/// Deliberately does NOT touch any WPF-bound collection itself (AllTasks, SelectedTask, ...) -
/// TaskSyncMerge.ComputeMergePlan decides WHAT changed (already pure/tested), and the caller
/// supplies <paramref name="applyMergePlan"/> to decide how that plan gets reflected into bound
/// UI state. That keeps this class testable without constructing a ViewModel, while the actual
/// collection mutation - which has to run on the UI thread and interacts with undo/recurrence via
/// TaskItem.PropertyChanged - stays exactly where it already correctly lives.
/// </summary>
public class SyncCoordinator
{
    private readonly GoogleDriveService _googleDrive;
    private readonly TodoStore _store;
    private readonly Settings _settings;
    private readonly SettingsStore _settingsStore;
    private bool _syncInProgress;

    public SyncCoordinator(GoogleDriveService googleDrive, TodoStore store, Settings settings, SettingsStore settingsStore)
    {
        _googleDrive = googleDrive;
        _store = store;
        _settings = settings;
        _settingsStore = settingsStore;
    }

    // See the original MainViewModel.MarkLegacyAttachmentsOwnerIfUnset: the first file a device
    // ever links to a pre-existing remote copy (rather than creating a brand-new one) is presumed
    // to be the one whose attachments already live in Drive's old shared flat layout, so that
    // layout keeps getting used for it instead of leaving real data behind under a new subfolder
    // nothing looks at. Only ever adopts a value once per device. Public so MainViewModel's own
    // AttachExistingGoogleDriveFileAsync (a file-session concern, not sync orchestration, so it
    // stays there) can still reach the same one-time-adoption rule.
    public void MarkLegacyAttachmentsOwnerIfUnset(string fileKey)
        => _settings.GoogleDriveLegacyAttachmentsFileKey ??= fileKey;

    // ROADMAP.md #57: reportProgress carries a coarse 0-100 completion estimate for the UI's
    // progress bar. There's no byte-level transfer telemetry cheaply available here (Drive's
    // .NET client library doesn't surface upload/download progress callbacks the way raw HttpClient
    // would), so this is stage-based rather than true byte progress - each call marks a real
    // pipeline step actually finishing, not a fabricated animation. Optional and defaulted so
    // every other caller (there are none today, but tests construct this directly) doesn't need
    // updating for a parameter it doesn't care about.
    public async Task PerformSyncAsync(
        AppState state,
        string currentFilePath,
        Func<Task> flushPendingSaveAsync,
        Func<AppState, (int Added, int Updated, int Removed, int Conflicted)> applyMergePlan,
        Action<string> reportStatus,
        Action promptForAuthentication,
        bool isSilentOnExit = false,
        Action<int>? reportProgress = null,
        Func<bool>? isFileSessionCurrent = null)
    {
        void Progress(int percent) => reportProgress?.Invoke(percent);

        // `state` is MainViewModel's one long-lived AppState, repopulated IN PLACE when a different
        // file is opened - so if the user opens file B while this pass is awaiting the network for
        // file A, every later use of `state` here would be B's tasks: A's remote content merged
        // into B, then saved to A's path and uploaded as A. Checked after each await that precedes
        // a read of `state`; a stale pass just stops (the newly opened file syncs on its own next
        // trigger). Optional so tests and any caller without a file-switching UI can omit it.
        bool AbandonIfFileChanged()
        {
            if (isFileSessionCurrent is null || isFileSessionCurrent()) return false;
            AppLogger.Info("SyncCoordinator", $"A different file was opened mid-sync - abandoning this pass for '{currentFilePath}'.");
            return true;
        }

        if (!_googleDrive.IsAuthenticated)
        {
            if (!isSilentOnExit) promptForAuthentication();
            return;
        }

        // Sync can be triggered by three independent, unrelated timers (edit-debounce, idle, and
        // the one-shot startup sync) plus manual "Sync Now" and exit, so two of them can land
        // close enough together to overlap - e.g. the idle timer fires right as an edit's
        // debounced sync also kicks off. Overlapping runs would race on the same local file and
        // remote state, so only one is allowed to actually run at a time; the others no-op and
        // whichever trigger fires next will just pick up the same work.
        if (_syncInProgress) return;
        _syncInProgress = true;

        try
        {
            // ROADMAP #126: folder resolution, media bookkeeping, and the file-ID cache below can
            // each call _settingsStore.Save independently during a single pass - batch them into
            // one real disk write when this scope closes instead of rewriting settings.json 5+
            // times per sync.
            using var settingsBatch = _settingsStore.BeginBatch();

            Progress(0);
            reportStatus("Syncing with Google Drive...");

            // The moment the state that gets uploaded was captured - the last instant at which
            // "local" and "what this pass puts on Drive" are known to agree. ComputeMergePlan uses
            // it to decide whether LOCAL changed since the two sides last agreed.
            // LastGoogleDriveSyncTime alone (stamped when the pass ENDS) got that wrong for any edit
            // made while a pass was running: not part of the upload, yet dated before the stamp, so
            // a later remote-wins merge saw it as "unchanged since last sync" and overwrote it with
            // no conflicted copy. Starts here (before the flush) and is moved up to each post-merge
            // save below, which is where the uploaded snapshot is really taken.
            var localBaselineUtc = DateTime.UtcNow;
            await flushPendingSaveAsync();
            Progress(10);
            var conflictedThisSync = 0;
            var mergedWithRemote = false;

            // Cache the remote file ID per local filename, not globally - a device can have more
            // than one .tasky file open over its lifetime (New/Open/Save As), and each one syncs
            // to its own remote file. A single global cache would hand a different file's remote
            // ID to whichever file happens to be open next, silently overwriting it on upload.
            var fileKey = Path.GetFileName(currentFilePath).ToLowerInvariant();
            var remoteId = _settings.GoogleDriveFileIdsByFile.TryGetValue(fileKey, out var cachedRemoteId)
                ? cachedRemoteId
                : null;

            // Legacy migration: versions before this fix cached a single global remote file ID.
            // If this file has never been synced under the new per-file cache AND the per-file
            // cache is otherwise empty (i.e. this device hasn't adopted the new scheme for any
            // file yet), the old global ID can only belong to whichever file was open when it was
            // last cached - almost always this same file, since most installs only ever use one.
            if (remoteId is null && !string.IsNullOrEmpty(_settings.GoogleDriveFileId) && _settings.GoogleDriveFileIdsByFile.Count == 0)
            {
                remoteId = _settings.GoogleDriveFileId;
                // This file is definitely the one the old flat attachments layout belongs to -
                // it's the exact file the legacy single-ID cache was tracking.
                MarkLegacyAttachmentsOwnerIfUnset(fileKey);
            }

            // This device has never linked to a remote file before (first-ever connect, or
            // reconnect after a disconnect) - resolve whether one already exists on Drive by
            // name before deciding what to do. Once a file ID is cached there's no need to touch
            // the "Tasky" folder lookup at all on this path - UploadFileAsync resolves (and
            // caches) it separately when it actually needs it.
            if (string.IsNullOrEmpty(remoteId))
            {
                var taskyFolderId = await _googleDrive.EnsureUsableTaskyFolderAsync(_settings, _settingsStore);
                remoteId = await _googleDrive.FindExistingFileIdAsync(Path.GetFileName(currentFilePath), taskyFolderId);
                if (!string.IsNullOrEmpty(remoteId))
                {
                    _settings.GoogleDriveFileIdsByFile[fileKey] = remoteId;
                    // Found real pre-existing data under this exact name (e.g. reconnecting after
                    // a disconnect, or a fresh install syncing the default filename) - same
                    // reasoning as the legacy-ID branch above, just reached a different way.
                    MarkLegacyAttachmentsOwnerIfUnset(fileKey);
                }
            }

            Progress(25);

            // A brand-new or just-emptied data file is never written to disk until the first
            // real edit triggers a save - flushPendingSaveAsync only flushes an edit that's
            // already pending, so it's a no-op here and UploadFileAsync would otherwise throw
            // FileNotFoundException trying to read a file that only ever existed in memory.
            if (AbandonIfFileChanged()) return;
            if (!File.Exists(currentFilePath))
                await _store.SaveAsync(state, currentFilePath);

            if (!string.IsNullOrEmpty(remoteId))
            {
                // A remote file already exists - merge it into local rather than guessing which
                // whole file is "newer." A device that's behind just adopts what's new, a device
                // with its own new tasks keeps them, and deletions propagate via tombstones
                // instead of a device that hasn't pulled a delete yet resurrecting it. The same
                // task edited on two devices since they last agreed still isn't fully field-level
                // merged, but neither edit is silently dropped - see TaskSyncMerge.ComputeMergePlan
                // and its ConflictedCopiesToAdd (ROADMAP.md #119).
                //
                // A remote file that's empty or not valid JSON (an interrupted upload, or a
                // leftover from some earlier failure) must not abort the whole sync - there's
                // nothing usable to merge, but local's own state is still good, so fall through
                // and let it upload normally rather than leaving the device stuck retrying a
                // sync that can never succeed against a file it can't read.
                var tempPath = Path.Combine(Path.GetTempPath(), $"tasky_remote_{Guid.NewGuid():N}.tasky");
                try
                {
                    // Drive has no conditional ("If-Match") upload, so download -> merge -> upload
                    // is a read-modify-write with nothing stopping another device uploading in the
                    // middle - its edits would then be overwritten by our upload without ever
                    // having been merged. Re-reading the head revision right before uploading and
                    // merging again if it moved shrinks that window from "the whole pass" to the
                    // moment between this check and the upload itself. Bounded, so two devices
                    // syncing in lockstep can't keep each other looping.
                    const int maxMergeRounds = 3;
                    for (var round = 1; round <= maxMergeRounds; round++)
                    {
                        var revisionAtDownload = await _googleDrive.GetHeadRevisionIdAsync(remoteId);
                        await _googleDrive.DownloadFileAsync(remoteId, tempPath, downloadAttachments: false);
                        var remoteState = await _store.LoadAsync(tempPath);
                        remoteState.DeletedTasks = TaskSyncMerge.DeduplicateTombstones(remoteState.DeletedTasks);
                        if (AbandonIfFileChanged()) return;
                        var (added, updated, removed, conflicted) = applyMergePlan(remoteState);
                        conflictedThisSync += conflicted;
                        AppLogger.Info("SyncCoordinator", $"Google Drive merge: +{added} task(s), ~{updated} updated, -{removed} removed" +
                            (conflicted > 0 ? $", {conflicted} conflicted cop{(conflicted == 1 ? "y" : "ies")} kept." : "."));

                        Progress(55);
                        // SaveAsync snapshots `state` synchronously as it's called - this is the
                        // exact content the upload below carries.
                        localBaselineUtc = DateTime.UtcNow;
                        await _store.SaveAsync(state, currentFilePath);
                        Progress(65);

                        var revisionNow = await _googleDrive.GetHeadRevisionIdAsync(remoteId);
                        if (revisionNow == revisionAtDownload) break;
                        AppLogger.Info("SyncCoordinator", $"Remote file changed during the merge (round {round}/{maxMergeRounds}) - merging the newer copy before uploading.");
                        if (AbandonIfFileChanged()) return;
                    }
                    mergedWithRemote = true;
                }
                catch (InvalidDataException ex)
                {
                    AppLogger.Warn("SyncCoordinator", $"Remote Google Drive file '{remoteId}' isn't readable ({ex.Message}) - skipping merge and uploading local state as-is.");
                }
                catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // The cached remote file ID no longer exists on Drive - the user deleted the
                    // Tasky folder (or just this file), or Settings.json still remembers a file
                    // from before a disconnect/reconnect. Same "stale cache, not a real failure"
                    // situation EnsureUsableTaskyFolderAsync already handles for a stale FOLDER
                    // ID (falls back to creating a fresh one); this is the missing counterpart for
                    // a stale FILE ID - DownloadFileAsync has no try/catch of its own around the
                    // actual Files.Get/download call, so this 404 would otherwise bubble all the
                    // way up past the catch above (InvalidDataException doesn't match a
                    // GoogleApiException) into the generic handler and surface as a scary "Google
                    // Drive sync failed" dialog on what should just be a self-healing first sync.
                    AppLogger.Warn("SyncCoordinator", $"Cached Google Drive file '{remoteId}' no longer exists (404) - clearing the stale reference and uploading local state as a new file.");
                    _settings.GoogleDriveFileIdsByFile.Remove(fileKey);
                    if (_settings.GoogleDriveFileId == remoteId) _settings.GoogleDriveFileId = null;
                    remoteId = null; // so the upload below creates a new file instead of retrying the dead one
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (IOException) { }
                }
            }

            // Upload the merged (or, on a first-ever sync anywhere, simply local) result.
            var newRemoteId = await _googleDrive.UploadFileAsync(currentFilePath, remoteId, _settings, _settingsStore);
            Progress(85);

            // The merge pulled in any new/updated Body blocks by JSON alone - a photo or file added
            // elsewhere (Tasky Web included) has its FileName reference now, but not yet the actual
            // bytes. Deliberately AFTER the upload: this can take minutes for large attachments,
            // and sitting between the download and the upload (where it used to be) it was by far
            // the widest part of the lost-update window described above. Cheap when nothing's new
            // (see SyncAttachmentsDownAsync), so not gated on what the merge actually added.
            if (mergedWithRemote)
                await _googleDrive.SyncAttachmentsDownAsync(currentFilePath, _settings, _settingsStore);
            Progress(95);
            _settings.GoogleDriveFileIdsByFile[fileKey] = newRemoteId;
            _settings.LastGoogleDriveSyncTime = DateTime.Now;
            _settings.LastGoogleDriveSyncLocalBaselineUtc = localBaselineUtc;
            _settingsStore.Save(_settings);
            Progress(100);

            reportStatus(conflictedThisSync > 0
                ? $"Synced - {conflictedThisSync} edit{(conflictedThisSync == 1 ? "" : "s")} conflicted with a remote change and " +
                  $"{(conflictedThisSync == 1 ? "was" : "were")} kept as \"(conflicted copy)\"."
                : "Successfully synced to Google Drive.");
        }
        catch (Google.GoogleApiException gEx) when (gEx.Message.Contains("disabled") || gEx.Message.Contains("has not been used"))
        {
            reportStatus("Google Drive API is disabled in your Google Cloud Console.");
            AppLogger.Error("SyncCoordinator", "Google Drive API disabled", gEx);
            if (!isSilentOnExit)
            {
                ThemedMessageBox.Show(
                    "Google Drive API is disabled in your Google Cloud Console project.\n\n" +
                    "Please click below to open Google Cloud Console and click 'ENABLE' on the Google Drive API page:\n" +
                    "https://console.developers.google.com/apis/api/drive.googleapis.com/overview?project=395690152006",
                    "Google Drive API Disabled",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            reportStatus($"Google Drive didn't sync: {ex.Message}");
            AppLogger.Error("SyncCoordinator", "Google Drive sync error", ex);
            if (!isSilentOnExit)
            {
                ThemedMessageBox.Show($"Google Drive didn't sync:\n{ex.Message}", "Google Drive Sync Problem", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _syncInProgress = false;
        }
    }
}
