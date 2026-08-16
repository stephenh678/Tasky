using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace TodoApp.Services;

public class GoogleDriveService
{
    private static readonly string[] Scopes = { DriveService.Scope.DriveFile };
    private const string ApplicationName = "Tasky Desktop App";
    private const string FolderMimeType = "application/vnd.google-apps.folder";

    private DriveService? _driveService;
    private UserCredential? _credential;
    // Folder IDs already confirmed usable (exists, not trashed) this run - avoids re-validating
    // the same cached "Tasky" folder on every single sync (auto-sync fires every 10s of editing,
    // idle sync every 3 minutes) once it's already been checked once.
    private readonly HashSet<string> _knownGoodFolderIds = new();

    public bool IsAuthenticated => _driveService is not null && _credential is not null;

    private static string TokenDataDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Tasky",
        "GoogleDriveToken");

    public const string DefaultClientId = "395690152006-u4b10m6lkqffllfpsa7imtu0mluibe13.apps.googleusercontent.com";
    public const string DefaultClientSecret = "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";

    /// <summary>
    /// Authenticates with Google Drive using OAuth.
    /// Opens the user's default browser to prompt for authorization if not already authorized.
    /// Uses built-in Desktop app credentials if custom ones are not provided.
    /// </summary>
    public async Task<bool> AuthenticateAsync(string? clientId = null, string? clientSecret = null, CancellationToken cancellationToken = default)
    {
        var activeClientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId.Trim();
        var activeClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? DefaultClientSecret : clientSecret.Trim();

        try
        {
            AppLogger.Info("GoogleDriveService", "Starting Google Drive authentication flow...");
            var secrets = new ClientSecrets
            {
                ClientId = activeClientId,
                ClientSecret = activeClientSecret
            };

            var tokenDataPath = TokenDataDataPath;
            Directory.CreateDirectory(tokenDataPath);

            _credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                Scopes,
                "user",
                cancellationToken,
                new FileDataStore(tokenDataPath, true));

            _driveService = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = _credential,
                ApplicationName = ApplicationName,
            });

            AppLogger.Info("GoogleDriveService", "Google Drive authentication successful.");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("GoogleDriveService", "Failed to authenticate with Google Drive", ex);
            _credential = null;
            _driveService = null;
            return false;
        }
    }

    /// <summary>
    /// Attempts to silently load stored OAuth tokens if present.
    /// </summary>
    public async Task<bool> TrySilentAuthenticateAsync(string? clientId = null, string? clientSecret = null)
    {
        var activeClientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId.Trim();
        var activeClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? DefaultClientSecret : clientSecret.Trim();

        var tokenPath = TokenDataDataPath;
        if (!Directory.Exists(tokenPath) || Directory.GetFiles(tokenPath).Length == 0)
            return false;

        try
        {
            var secrets = new ClientSecrets
            {
                ClientId = activeClientId,
                ClientSecret = activeClientSecret
            };

            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                Scopes,
                "user",
                CancellationToken.None,
                new FileDataStore(tokenPath, true));

            if (credential.Token is null || credential.Token.IsStale)
            {
                var refreshed = await credential.RefreshTokenAsync(CancellationToken.None);
                if (!refreshed) return false;
            }

            _credential = credential;
            _driveService = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = _credential,
                ApplicationName = ApplicationName,
            });

            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("GoogleDriveService", $"Silent authentication failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets the primary email address of the authenticated Google user.
    /// </summary>
    public async Task<string?> GetAccountEmailAsync()
    {
        if (_driveService is null) return null;

        try
        {
            var request = _driveService.About.Get();
            request.Fields = "user(emailAddress,displayName)";
            var about = await request.ExecuteAsync();
            return about.User?.EmailAddress;
        }
        catch (Exception ex)
        {
            AppLogger.Error("GoogleDriveService", "Failed to get account email", ex);
            return null;
        }
    }

    /// <summary>
    /// Finds or creates a 'Tasky' folder in Google Drive.
    /// </summary>
    public async Task<string> GetOrCreateFolderAsync(string folderName, string? parentFolderId = null)
    {
        if (_driveService is null)
            throw new InvalidOperationException("Not authenticated with Google Drive.");

        var q = $"name = '{folderName}' and mimeType = '{FolderMimeType}' and trashed = false";
        if (!string.IsNullOrEmpty(parentFolderId))
            q += $" and '{parentFolderId}' in parents";

        // A folder that was genuinely just created by another device can briefly not show up in
        // a files.list query - Drive's search index lags the write by a couple of seconds. Retry
        // a few times before concluding it doesn't exist; otherwise a second device's first-ever
        // connect can race this and create a duplicate "Tasky" folder instead of finding the real
        // one (observed live: 3 separate duplicate folders got created this way in one evening).
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var listRequest = _driveService.Files.List();
            listRequest.Q = q;
            listRequest.Fields = "files(id, name)";
            var result = await listRequest.ExecuteAsync();

            if (result.Files != null && result.Files.Count > 0)
            {
                return result.Files[0].Id;
            }

            if (attempt < maxAttempts)
            {
                AppLogger.Warn("GoogleDriveService", $"Folder '{folderName}' not found on attempt {attempt}/{maxAttempts} - retrying in case Drive's index is still catching up.");
                await Task.Delay(1500);
            }
        }

        // Create new folder
        var folderMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = folderName,
            MimeType = FolderMimeType,
            Parents = !string.IsNullOrEmpty(parentFolderId) ? new List<string> { parentFolderId } : null
        };

        var createRequest = _driveService.Files.Create(folderMetadata);
        createRequest.Fields = "id";
        var created = await createRequest.ExecuteAsync();
        AppLogger.Info("GoogleDriveService", $"Created Google Drive folder '{folderName}' with ID '{created.Id}'");
        return created.Id;
    }

    /// <summary>
    /// Resolves the cached "Tasky" root folder, but first makes sure it's still real and not
    /// sitting in Trash before trusting it. Drive doesn't reject uploads into a trashed folder's
    /// subtree - it just hides everything under it from normal browsing - so a cached ID whose
    /// folder got trashed after the fact would otherwise make every future sync look successful
    /// while silently writing into a folder nobody can see anymore. A trashed (or permanently
    /// deleted) cached folder is never restored - there's no reliable way to tell an accidental
    /// trash from a deliberate one (e.g. resetting for a fresh-install test), so this always
    /// treats it as gone and resolves a fresh one instead via the normal find-or-create-by-name
    /// path. That still converges multiple devices onto the same folder without duplicating it:
    /// whichever device gets there first creates it, and every other device's by-name search
    /// finds and reuses that same real folder instead of each creating its own.
    /// Only touches the network once per folder ID per app run - already-validated IDs are free.
    /// </summary>
    /// <param name="anchorFileId">
    /// If the folder ID isn't cached yet but a remote file we already know about is, prefer
    /// discovering the folder from where that file actually lives rather than a blind by-name
    /// search - a by-name search can land on a *different* duplicate "Tasky" folder than the one
    /// the file is really sitting in.
    /// </param>
    public async Task<string> EnsureUsableTaskyFolderAsync(Settings settings, SettingsStore settingsStore, string? anchorFileId = null)
    {
        var cachedId = settings.GoogleDriveFolderId;
        if (string.IsNullOrEmpty(cachedId))
        {
            cachedId = !string.IsNullOrEmpty(anchorFileId) ? await GetParentFolderIdAsync(anchorFileId) : null;
            cachedId ??= await GetOrCreateFolderAsync("Tasky");
            settings.GoogleDriveFolderId = cachedId;
            settingsStore.Save(settings);
            _knownGoodFolderIds.Add(cachedId);
            return cachedId;
        }

        if (_knownGoodFolderIds.Contains(cachedId)) return cachedId;
        if (_driveService is null) return cachedId;

        var isUsable = false;
        try
        {
            var getRequest = _driveService.Files.Get(cachedId);
            getRequest.Fields = "id, trashed";
            var folder = await getRequest.ExecuteAsync();
            isUsable = folder.Trashed != true;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            isUsable = false;
        }

        if (isUsable)
        {
            _knownGoodFolderIds.Add(cachedId);
            return cachedId;
        }

        AppLogger.Warn("GoogleDriveService", $"Cached Tasky folder '{cachedId}' is trashed or gone - resolving a fresh one instead of restoring it.");
        var freshId = await GetOrCreateFolderAsync("Tasky");
        settings.GoogleDriveFolderId = freshId;
        settingsStore.Save(settings);
        _knownGoodFolderIds.Add(freshId);
        return freshId;
    }

    /// <summary>
    /// Looks up a file by name within a Drive folder without creating one, for callers that
    /// need to know whether existing remote content is present before deciding what to do.
    /// </summary>
    public async Task<string?> FindExistingFileIdAsync(string fileName, string parentFolderId)
    {
        if (_driveService is null) return null;

        var lookupRequest = _driveService.Files.List();
        lookupRequest.Q = $"name = '{fileName}' and '{parentFolderId}' in parents and trashed = false";
        lookupRequest.Fields = "files(id, name)";
        var existingFiles = (await lookupRequest.ExecuteAsync()).Files;
        return existingFiles is { Count: > 0 } ? existingFiles[0].Id : null;
    }

    /// <summary>
    /// Looks up the parent folder ID a given file actually lives in.
    /// </summary>
    private async Task<string?> GetParentFolderIdAsync(string fileId)
    {
        if (_driveService is null) return null;

        var getRequest = _driveService.Files.Get(fileId);
        getRequest.Fields = "parents";
        var file = await getRequest.ExecuteAsync();
        return file.Parents is { Count: > 0 } ? file.Parents[0] : null;
    }

    /// <summary>
    /// Uploads or updates a .tasky file in Google Drive under the 'Tasky' folder.
    /// </summary>
    public async Task<string> UploadFileAsync(string localPath, string? existingRemoteFileId = null, Settings? settings = null, SettingsStore? settingsStore = null)
    {
        if (_driveService is null)
            throw new InvalidOperationException("Not authenticated with Google Drive.");

        if (!File.Exists(localPath))
            throw new FileNotFoundException("Local file does not exist.", localPath);

        AppLogger.Info("GoogleDriveService", $"Uploading file '{localPath}' to Google Drive");

        // Cache the resolved "Tasky" folder ID once found, instead of re-resolving it by name on
        // every single sync - besides the wasted round-trip, each fresh lookup is one more chance
        // to hit the index-lag race in GetOrCreateFolderAsync and spawn a duplicate folder. Once
        // cached, EnsureUsableTaskyFolderAsync still validates it isn't sitting in Trash (see its
        // doc comment) rather than trusting it blindly forever.
        string taskyFolderId;
        if (settings is not null && settingsStore is not null)
        {
            taskyFolderId = await EnsureUsableTaskyFolderAsync(settings, settingsStore, existingRemoteFileId);
        }
        else
        {
            // If we already know exactly which remote file this is, ask Drive where that file
            // actually lives instead of a separate by-name folder search - a by-name search can
            // resolve to a *different* duplicate "Tasky" folder than the one the cached file is
            // really sitting in (observed live: a device with a stale cached file ID cached a
            // folder ID that didn't match). Only fall back to name-based lookup/creation when
            // there's no existing file yet to anchor to (this device's first-ever upload).
            taskyFolderId = (!string.IsNullOrEmpty(existingRemoteFileId)
                ? await GetParentFolderIdAsync(existingRemoteFileId)
                : null) ?? await GetOrCreateFolderAsync("Tasky");
        }
        var fileName = Path.GetFileName(localPath);
        using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // The caller's cached ID isn't the only way an existing file can be found - it's lost on
        // disconnect, and a second device's very first connect never has one at all. Without a
        // name-based fallback (unlike GetOrCreateFolderAsync, which already does this for
        // folders), either case would silently create a duplicate Tasky.tasky instead of finding
        // the one that's already there.
        var resolvedRemoteId = existingRemoteFileId;
        if (string.IsNullOrEmpty(resolvedRemoteId))
        {
            resolvedRemoteId = await FindExistingFileIdAsync(fileName, taskyFolderId);
            if (!string.IsNullOrEmpty(resolvedRemoteId))
                AppLogger.Info("GoogleDriveService", $"Found existing remote file '{fileName}' (ID '{resolvedRemoteId}') by name - reusing instead of creating a duplicate");
        }

        if (!string.IsNullOrEmpty(resolvedRemoteId))
        {
            try
            {
                var updateBody = new Google.Apis.Drive.v3.Data.File { Name = fileName };
                var updateRequest = _driveService.Files.Update(updateBody, resolvedRemoteId, fileStream, "application/json");
                var progress = await updateRequest.UploadAsync();

                if (progress.Status == Google.Apis.Upload.UploadStatus.Completed)
                {
                    AppLogger.Info("GoogleDriveService", $"Successfully updated existing remote file ID '{resolvedRemoteId}'");
                    await SyncAttachmentsAsync(localPath, taskyFolderId, settings, settingsStore);
                    return resolvedRemoteId;
                }

                if (progress.Exception is not null) throw progress.Exception;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("GoogleDriveService", $"Could not update existing file '{resolvedRemoteId}', creating new: {ex.Message}");
            }
        }

        // Create new file inside Tasky folder
        var newFileBody = new Google.Apis.Drive.v3.Data.File
        {
            Name = fileName,
            MimeType = "application/json",
            Description = "Tasky application task file",
            Parents = new List<string> { taskyFolderId }
        };

        var createRequest = _driveService.Files.Create(newFileBody, fileStream, "application/json");
        createRequest.Fields = "id";
        var createProgress = await createRequest.UploadAsync();

        if (createProgress.Status == Google.Apis.Upload.UploadStatus.Completed && createRequest.ResponseBody is not null)
        {
            var newId = createRequest.ResponseBody.Id;
            AppLogger.Info("GoogleDriveService", $"Successfully created new Google Drive file with ID '{newId}' in Tasky folder");
            await SyncAttachmentsAsync(localPath, taskyFolderId, settings, settingsStore);
            return newId;
        }

        if (createProgress.Exception is not null)
            throw createProgress.Exception;

        throw new IOException("Failed to upload file to Google Drive.");
    }

    /// <summary>
    /// Syncs task attachment photos and files between local media directories (Attachments &amp; InlineImages) and Google Drive using 3-way diff.
    /// </summary>
    private async Task SyncAttachmentsAsync(string localDataFilePath, string taskyFolderId, Settings? settings = null, SettingsStore? settingsStore = null)
    {
        if (_driveService is null) return;

        var containerFolderId = await ResolveMediaContainerFolderIdAsync(localDataFilePath, taskyFolderId, settings, settingsStore);

        await SyncMediaDirectoryAsync("Attachments", localDataFilePath, containerFolderId, settings, settingsStore);
        await SyncMediaDirectoryAsync("InlineImages", localDataFilePath, containerFolderId, settings, settingsStore);
    }

    /// <summary>
    /// Resolves (and caches) the Drive folder that holds one specific local .tasky file's own
    /// "Attachments"/"InlineImages" subfolders. Before this existed, every file's attachments
    /// lived flat in the shared root "Tasky" folder, so two different .tasky files' attachments
    /// (and the 3-way-diff "was this in the last sync" bookkeeping) could collide. The one file
    /// that already has real data sitting in that flat layout keeps using it; every other file
    /// gets its own subfolder so they can never collide.
    /// </summary>
    private async Task<string> ResolveMediaContainerFolderIdAsync(string localDataFilePath, string taskyFolderId, Settings? settings, SettingsStore? settingsStore)
    {
        if (settings is null) return taskyFolderId;

        var fileKey = Path.GetFileName(localDataFilePath).ToLowerInvariant();
        if (settings.GoogleDriveMediaContainerFolderIdsByFile.TryGetValue(fileKey, out var cached))
            return cached;

        // "Tasky.tasky" is every install's default filename, and Path.GetFileNameWithoutExtension
        // of that is literally "Tasky" - the exact same name as the root folder itself. Using that
        // bare name for a non-legacy file's subfolder creates a "Tasky" folder nested inside
        // "Tasky", which reads as a confusing duplicate rather than a per-file container. Appending
        // a suffix guarantees this can never collide with the root folder's own name.
        var containerId = !string.IsNullOrEmpty(settings.GoogleDriveLegacyAttachmentsFileKey)
                           && settings.GoogleDriveLegacyAttachmentsFileKey == fileKey
            ? taskyFolderId
            : await GetOrCreateFolderAsync($"{Path.GetFileNameWithoutExtension(fileKey)} Attachments", taskyFolderId);

        settings.GoogleDriveMediaContainerFolderIdsByFile[fileKey] = containerId;
        settingsStore?.Save(settings);
        return containerId;
    }

    private async Task SyncMediaDirectoryAsync(string dirName, string localDataFilePath, string containerFolderId, Settings? settings = null, SettingsStore? settingsStore = null)
    {
        if (_driveService is null) return;

        try
        {
            var baseDir = Path.GetDirectoryName(localDataFilePath) ?? ".";
            var localDir = Path.Combine(baseDir, dirName);
            Directory.CreateDirectory(localDir);

            var remoteFolderId = await GetOrCreateFolderAsync(dirName, containerFolderId);

            // Fetch remote files on Google Drive under dirName
            var listReq = _driveService.Files.List();
            listReq.Q = $"'{remoteFolderId}' in parents and trashed = false";
            listReq.Fields = "files(id, name, modifiedTime)";
            var remoteFiles = (await listReq.ExecuteAsync()).Files ?? new List<Google.Apis.Drive.v3.Data.File>();
            var remoteFileDict = remoteFiles
                .Where(f => !string.IsNullOrEmpty(f.Name))
                .ToDictionary(f => f.Name!, StringComparer.OrdinalIgnoreCase);

            var currentLocalFiles = Directory.GetFiles(localDir)
                .Select(f => Path.GetFileName(f)!)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var referencedFiles = GetReferencedAttachmentFilenames(localDataFilePath);
            var mediaFileKey = Path.GetFileName(localDataFilePath).ToLowerInvariant();
            var lastSyncedSet = ResolveLastSyncedMediaSet(mediaFileKey, settings);

            // 1. Process Remote Files with 3-Way Diff logic
            foreach (var rFile in remoteFiles)
            {
                if (string.IsNullOrEmpty(rFile.Name)) continue;
                var fileName = rFile.Name;
                bool existsLocally = currentLocalFiles.Contains(fileName);
                bool isReferencedInTaskData = referencedFiles.Contains(fileName);
                bool wasInLastSync = lastSyncedSet.Contains(fileName);

                // Case A: Was present in last sync, but deleted locally on this device since last sync (and not in task data)
                if (wasInLastSync && !existsLocally && !isReferencedInTaskData)
                {
                    try
                    {
                        await _driveService.Files.Delete(rFile.Id).ExecuteAsync();
                        AppLogger.Info("GoogleDriveService", $"3-Way Diff: Pruned deleted {dirName} file '{fileName}' from Google Drive");
                        lastSyncedSet.Remove(fileName);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("GoogleDriveService", $"Failed to delete remote {dirName} file '{fileName}': {ex.Message}");
                    }
                    continue;
                }

                // Case B: Present on Drive, but NOT in last sync and NOT on local disk -> Added by another device!
                if (!existsLocally)
                {
                    var localFilePath = Path.Combine(localDir, fileName);
                    try
                    {
                        using var stream = new FileStream(localFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
                        var getReq = _driveService.Files.Get(rFile.Id);
                        await getReq.DownloadAsync(stream);
                        AppLogger.Info("GoogleDriveService", $"3-Way Diff: Downloaded remote {dirName} file '{fileName}' (added from another device)");
                        currentLocalFiles.Add(fileName);
                        lastSyncedSet.Add(fileName);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("GoogleDriveService", $"Failed to download remote {dirName} file '{fileName}': {ex.Message}");
                    }
                    continue;
                }

                // Case C: File exists both locally and remotely
                lastSyncedSet.Add(fileName);
            }

            // 2. Upload Local-Only Files (added on this device since last sync)
            foreach (var localFile in Directory.GetFiles(localDir))
            {
                var fileName = Path.GetFileName(localFile);
                if (!remoteFileDict.ContainsKey(fileName))
                {
                    try
                    {
                        using var stream = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var body = new Google.Apis.Drive.v3.Data.File
                        {
                            Name = fileName,
                            Parents = new List<string> { remoteFolderId }
                        };
                        var uploadReq = _driveService.Files.Create(body, stream, "application/octet-stream");
                        await uploadReq.UploadAsync();
                        AppLogger.Info("GoogleDriveService", $"3-Way Diff: Uploaded local {dirName} file '{fileName}' to Google Drive");
                        lastSyncedSet.Add(fileName);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("GoogleDriveService", $"Failed to upload local {dirName} file '{fileName}': {ex.Message}");
                    }
                }
            }

            // Save updated lastSyncedSet back to settings if provided
            if (settings is not null && settingsStore is not null)
            {
                settings.LastSyncedMediaFilesByFile[mediaFileKey] = lastSyncedSet.ToList();
                settingsStore.Save(settings);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("GoogleDriveService", $"SyncMediaDirectoryAsync error for {dirName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Same legacy-preservation logic as ResolveMediaContainerFolderIdAsync, for the "was this
    /// file part of the last sync" bookkeeping instead of the folder location: the file that owns
    /// the old flat layout inherits its real accumulated history so nothing looks newly-added (or
    /// worse, looks deleted-and-needing-pruning) on the first sync under the new per-file scheme.
    /// </summary>
    private static HashSet<string> ResolveLastSyncedMediaSet(string fileKey, Settings? settings)
    {
        if (settings is null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (settings.LastSyncedMediaFilesByFile.TryGetValue(fileKey, out var existing))
            return new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(settings.GoogleDriveLegacyAttachmentsFileKey) && settings.GoogleDriveLegacyAttachmentsFileKey == fileKey)
            return new HashSet<string>(settings.LastSyncedMediaFiles, StringComparer.OrdinalIgnoreCase);

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> GetReferencedAttachmentFilenames(string localDataFilePath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(localDataFilePath))
            {
                var content = File.ReadAllText(localDataFilePath);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                // Handle AppState object {"Tasks": [...]} AND raw array fallback
                JsonElement tasksElem = default;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Tasks", out tasksElem) && tasksElem.ValueKind == JsonValueKind.Array)
                {
                    // Found AppState Tasks array
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    tasksElem = root;
                }

                if (tasksElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var taskElem in tasksElem.EnumerateArray())
                    {
                        if (taskElem.TryGetProperty("Body", out var bodyElem) && bodyElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in bodyElem.EnumerateArray())
                            {
                                ParseBlockReferences(block, set);
                            }
                        }
                        if (taskElem.TryGetProperty("NoteBlocks", out var blocksElem) && blocksElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in blocksElem.EnumerateArray())
                            {
                                ParseBlockReferences(block, set);
                            }
                        }
                    }
                }

                // Scan full raw JSON text for any image GUIDs or attachment filenames
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(content, @"[a-zA-Z0-9_\-]{3,}\.(png|jpg|jpeg|gif|bmp|pdf|docx|xlsx|zip|txt)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    set.Add(match.Value);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("GoogleDriveService", $"GetReferencedAttachmentFilenames error: {ex.Message}");
        }
        return set;
    }

    private static void ParseBlockReferences(JsonElement block, HashSet<string> set)
    {
        if (block.TryGetProperty("PhotoPath", out var pProp) && pProp.ValueKind == JsonValueKind.String)
        {
            var pName = Path.GetFileName(pProp.GetString());
            if (!string.IsNullOrEmpty(pName)) set.Add(pName);
        }
        if (block.TryGetProperty("FilePath", out var fProp) && fProp.ValueKind == JsonValueKind.String)
        {
            var fName = Path.GetFileName(fProp.GetString());
            if (!string.IsNullOrEmpty(fName)) set.Add(fName);
        }
        if (block.TryGetProperty("Rtf", out var rtfProp) && rtfProp.ValueKind == JsonValueKind.String)
        {
            var rtf = rtfProp.GetString() ?? "";
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(rtf, @"[a-zA-Z0-9_\-]{3,}\.(png|jpg|jpeg|gif|bmp|pdf|docx|xlsx|zip|txt)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                set.Add(match.Value);
            }
        }
    }

    /// <summary>
    /// Downloads a .tasky file and its attachments from Google Drive to the local path.
    /// </summary>
    public async Task DownloadFileAsync(string remoteFileId, string destinationLocalPath, bool downloadAttachments = true, Settings? settings = null, SettingsStore? settingsStore = null)
    {
        if (_driveService is null)
            throw new InvalidOperationException("Not authenticated with Google Drive.");

        AppLogger.Info("GoogleDriveService", $"Downloading remote file ID '{remoteFileId}' to '{destinationLocalPath}'");

        var dir = Path.GetDirectoryName(destinationLocalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var request = _driveService.Files.Get(remoteFileId);
        using var memoryStream = new MemoryStream();
        await request.DownloadAsync(memoryStream);

        memoryStream.Position = 0;
        await File.WriteAllBytesAsync(destinationLocalPath, memoryStream.ToArray());
        AppLogger.Info("GoogleDriveService", $"Download completed successfully for '{destinationLocalPath}'");

        if (!downloadAttachments) return;

        // Download attachments from this file's own Attachments/InlineImages folder on Google
        // Drive (the shared flat root for the legacy file, a dedicated subfolder otherwise - see
        // ResolveMediaContainerFolderIdAsync).
        try
        {
            var taskyFolderId = await GetOrCreateFolderAsync("Tasky");
            var containerFolderId = await ResolveMediaContainerFolderIdAsync(destinationLocalPath, taskyFolderId, settings, settingsStore);
            await DownloadMediaDirectoryAsync("Attachments", dir ?? ".", containerFolderId);
            await DownloadMediaDirectoryAsync("InlineImages", dir ?? ".", containerFolderId);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("GoogleDriveService", $"Error downloading attachments: {ex.Message}");
        }
    }

    private async Task DownloadMediaDirectoryAsync(string dirName, string targetBaseDir, string taskyFolderId)
    {
        if (_driveService is null) return;
        try
        {
            var remoteFolderId = await GetOrCreateFolderAsync(dirName, taskyFolderId);
            var localDir = Path.Combine(targetBaseDir, dirName);
            Directory.CreateDirectory(localDir);

            var filesReq = _driveService.Files.List();
            filesReq.Q = $"'{remoteFolderId}' in parents and trashed = false";
            filesReq.Fields = "files(id, name)";
            var remoteFiles = (await filesReq.ExecuteAsync()).Files ?? new List<Google.Apis.Drive.v3.Data.File>();

            foreach (var rFile in remoteFiles)
            {
                if (string.IsNullOrEmpty(rFile.Name)) continue;
                var destFile = Path.Combine(localDir, rFile.Name);
                if (!File.Exists(destFile))
                {
                    var dlReq = _driveService.Files.Get(rFile.Id);
                    using var ms = new MemoryStream();
                    await dlReq.DownloadAsync(ms);
                    await File.WriteAllBytesAsync(destFile, ms.ToArray());
                    AppLogger.Info("GoogleDriveService", $"Downloaded {dirName} file '{rFile.Name}' to '{destFile}'");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("GoogleDriveService", $"Error downloading {dirName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the modified timestamp of a file on Google Drive.
    /// </summary>
    public async Task<DateTime?> GetRemoteModifiedTimeAsync(string remoteFileId)
    {
        if (_driveService is null || string.IsNullOrEmpty(remoteFileId)) return null;

        try
        {
            var request = _driveService.Files.Get(remoteFileId);
            request.Fields = "id, name, modifiedTime";
            var file = await request.ExecuteAsync();
            return file.ModifiedTimeDateTimeOffset?.LocalDateTime;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("GoogleDriveService", $"Failed to get remote modified time for ID '{remoteFileId}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Lists .tasky files present in the user's Google Drive.
    /// </summary>
    public async Task<List<Google.Apis.Drive.v3.Data.File>> ListTaskyFilesAsync()
    {
        if (_driveService is null) return new List<Google.Apis.Drive.v3.Data.File>();

        try
        {
            var request = _driveService.Files.List();
            // Drive's "contains" operator on name is word/prefix-tokenized, not a literal
            // substring match - it ignores the dot, so "name contains '.tasky'" alone also
            // matches a folder or file named just "Tasky" (e.g. the root Tasky sync folder, or a
            // leftover duplicate folder). Exclude folders in the query, then re-check the exact
            // ".tasky" suffix client-side as a second guard regardless of Drive's matching quirks.
            request.Q = "name contains '.tasky' and trashed = false and mimeType != 'application/vnd.google-apps.folder'";
            request.Fields = "files(id, name, modifiedTime, size)";
            var result = await request.ExecuteAsync();
            return (result.Files ?? new List<Google.Apis.Drive.v3.Data.File>())
                .Where(f => !string.IsNullOrEmpty(f.Name) && f.Name.EndsWith(".tasky", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex)
        {
            AppLogger.Error("GoogleDriveService", "Failed to list Google Drive files", ex);
            return new List<Google.Apis.Drive.v3.Data.File>();
        }
    }

    /// <summary>
    /// Revokes authorization tokens and signs out of Google Drive.
    /// </summary>
    public async Task SignOutAsync()
    {
        try
        {
            if (_credential is not null)
            {
                await _credential.RevokeTokenAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("GoogleDriveService", $"Error revoking token during sign out: {ex.Message}");
        }
        finally
        {
            _credential = null;
            _driveService = null;

            try
            {
                if (Directory.Exists(TokenDataDataPath))
                    Directory.Delete(TokenDataDataPath, recursive: true);
            }
            catch (Exception ex)
            {
                AppLogger.Warn("GoogleDriveService", $"Could not delete token directory: {ex.Message}");
            }
        }
    }
}
