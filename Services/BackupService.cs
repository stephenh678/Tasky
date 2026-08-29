using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using TodoApp.Behaviors;
using TodoApp.Models;

namespace TodoApp.Services;

/// <summary>
/// Exports the current data file plus every attachment it actually references into a single
/// portable .zip, and restores one back - a full "move to a new machine" backup. Distinct from
/// the automatic Backups\ folder (which only ever snapshots the .tasky JSON, never attachments)
/// and from Google Drive sync (which needs to be signed in and online).
/// </summary>
public static class BackupService
{
    private const string AttachmentsEntryPrefix = "Attachments/";

    /// <summary>
    /// Every attachment filename referenced anywhere in this task: explicit Photo/File blocks,
    /// plus images and files embedded inline in any block's Rtf (pasted images / Insert File both
    /// land in a block's Rtf rather than a separate Photo/File block - see editor.js's
    /// extractInlineImageFileNames/extractInlineFileNames for the same detection on the web side).
    /// </summary>
    public static IEnumerable<string> CollectReferencedFileNames(TaskItem task)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in task.Body)
            TaskMediaHelper.CollectReferencedFileNames(block.PhotoPath, block.Rtf, names);
        return names;
    }

    /// <summary>
    /// Creates a full backup zip: the data file itself plus every attachment its tasks reference,
    /// resolved from wherever it actually lives locally. Returns how many attachments were found
    /// vs. referenced-but-missing, so the caller can tell the user if anything was left out.
    /// </summary>
    public static (int included, int missing) Export(string dataFilePath, IEnumerable<TaskItem> tasks, string zipPath)
    {
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
            foreach (var name in CollectReferencedFileNames(task))
                fileNames.Add(name);

        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        zip.CreateEntryFromFile(dataFilePath, Path.GetFileName(dataFilePath));

        int included = 0, missing = 0;
        foreach (var fileName in fileNames)
        {
            var localPath = ResolveAnyLocalPath(dataFilePath, fileName);
            if (localPath is null)
            {
                missing++;
                continue;
            }
            zip.CreateEntryFromFile(localPath, AttachmentsEntryPrefix + fileName);
            included++;
        }
        return (included, missing);
    }

    // Checks the current MediaPathResolver-resolved Attachments/InlineImages folders first, then
    // falls back to the legacy per-task Attachments\{taskId}\ subfolder layout that older Tasky
    // versions wrote next to the data file, so backups created back then still restore correctly.
    private static string? ResolveAnyLocalPath(string dataFilePath, string fileName)
    {
        var attachmentsPath = Path.Combine(RichTextBoxBehavior.GetAttachmentsDirectory(), fileName);
        if (File.Exists(attachmentsPath)) return attachmentsPath;

        var inlinePath = Path.Combine(RichTextBoxBehavior.GetInlineAttachmentDirectory(), fileName);
        if (File.Exists(inlinePath)) return inlinePath;

        var perTaskRoot = Path.Combine(Path.GetDirectoryName(dataFilePath) ?? ".", "Attachments");
        if (Directory.Exists(perTaskRoot))
        {
            foreach (var taskDir in Directory.GetDirectories(perTaskRoot))
            {
                var candidate = Path.Combine(taskDir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts a backup zip to a temp folder and returns the path to the .tasky file inside it
    /// plus every attachment file that came with it. Caller decides whether/how to actually apply
    /// it (RestoreAttachments + TodoStore.RestoreBackup) once the user confirms.
    /// </summary>
    public static (string dataFilePath, IReadOnlyList<string> attachmentFiles) ExtractToTemp(string zipPath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "TaskyImport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        ZipFile.ExtractToDirectory(zipPath, tempDir);

        var dataFile = Directory.GetFiles(tempDir, "*.tasky").FirstOrDefault()
            ?? throw new InvalidDataException("This .zip doesn't contain a .tasky data file - it may not be a Tasky backup.");

        var attachmentsDir = Path.Combine(tempDir, "Attachments");
        var attachmentFiles = Directory.Exists(attachmentsDir)
            ? Directory.GetFiles(attachmentsDir)
            : Array.Empty<string>();

        return (dataFile, attachmentFiles);
    }

    /// <summary>
    /// Copies extracted attachment files into the local Attachments folder, overwriting anything
    /// already there with the same name since this is an explicit restore. Landing everything in
    /// one place is fine even for files that started out in InlineImages or a per-task folder -
    /// ResolveLocalAttachmentPath already checks both Attachments and InlineImages for any file.
    /// </summary>
    public static void RestoreAttachments(IEnumerable<string> attachmentFiles)
    {
        var dest = RichTextBoxBehavior.GetAttachmentsDirectory();
        foreach (var file in attachmentFiles)
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
    }
}
