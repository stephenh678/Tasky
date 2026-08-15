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

        var listRequest = _driveService.Files.List();
        listRequest.Q = q;
        listRequest.Fields = "files(id, name)";
        var result = await listRequest.ExecuteAsync();

        if (result.Files != null && result.Files.Count > 0)
        {
            return result.Files[0].Id;
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
    /// Uploads or updates a .tasky file in Google Drive under the 'Tasky' folder.
    /// </summary>
    public async Task<string> UploadFileAsync(string localPath, string? existingRemoteFileId = null, Settings? settings = null, SettingsStore? settingsStore = null)
    {
        if (_driveService is null)
            throw new InvalidOperationException("Not authenticated with Google Drive.");

        if (!File.Exists(localPath))
            throw new FileNotFoundException("Local file does not exist.", localPath);

        AppLogger.Info("GoogleDriveService", $"Uploading file '{localPath}' to Google Drive");

        var taskyFolderId = await GetOrCreateFolderAsync("Tasky");
        var fileName = Path.GetFileName(localPath);
        using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (!string.IsNullOrEmpty(existingRemoteFileId))
        {
            try
            {
                var updateBody = new Google.Apis.Drive.v3.Data.File { Name = fileName };
                var updateRequest = _driveService.Files.Update(updateBody, existingRemoteFileId, fileStream, "application/json");
                var progress = await updateRequest.UploadAsync();

                if (progress.Status == Google.Apis.Upload.UploadStatus.Completed)
                {
                    AppLogger.Info("GoogleDriveService", $"Successfully updated existing remote file ID '{existingRemoteFileId}'");
                    await SyncAttachmentsAsync(localPath, taskyFolderId, settings, settingsStore);
                    return existingRemoteFileId;
                }

                if (progress.Exception is not null) throw progress.Exception;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("GoogleDriveService", $"Could not update existing file '{existingRemoteFileId}', creating new: {ex.Message}");
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

        await SyncMediaDirectoryAsync("Attachments", localDataFilePath, taskyFolderId, settings, settingsStore);
        await SyncMediaDirectoryAsync("InlineImages", localDataFilePath, taskyFolderId, settings, settingsStore);
    }

    private async Task SyncMediaDirectoryAsync(string dirName, string localDataFilePath, string taskyFolderId, Settings? settings = null, SettingsStore? settingsStore = null)
    {
        if (_driveService is null) return;

        try
        {
            var baseDir = Path.GetDirectoryName(localDataFilePath) ?? ".";
            var localDir = Path.Combine(baseDir, dirName);
            Directory.CreateDirectory(localDir);

            var remoteFolderId = await GetOrCreateFolderAsync(dirName, taskyFolderId);

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
            var lastSyncedSet = settings?.LastSyncedMediaFiles is not null
                ? new HashSet<string>(settings.LastSyncedMediaFiles, StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                settings.LastSyncedMediaFiles = lastSyncedSet.ToList();
                settingsStore.Save(settings);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("GoogleDriveService", $"SyncMediaDirectoryAsync error for {dirName}: {ex.Message}");
        }
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
    public async Task DownloadFileAsync(string remoteFileId, string destinationLocalPath)
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

        // Download attachments from Tasky/Attachments and Tasky/InlineImages on Google Drive
        try
        {
            var taskyFolderId = await GetOrCreateFolderAsync("Tasky");
            await DownloadMediaDirectoryAsync("Attachments", dir ?? ".", taskyFolderId);
            await DownloadMediaDirectoryAsync("InlineImages", dir ?? ".", taskyFolderId);
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
            request.Q = "name contains '.tasky' and trashed = false";
            request.Fields = "files(id, name, modifiedTime, size)";
            var result = await request.ExecuteAsync();
            return result.Files?.ToList() ?? new List<Google.Apis.Drive.v3.Data.File>();
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
