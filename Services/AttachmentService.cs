using System;
using System.IO;

namespace TodoApp.Services;

public class AttachmentService
{
    private string _rootFolder;

    public AttachmentService(string dataFilePath)
    {
        _rootFolder = ComputeRoot(dataFilePath);
    }

    // Attachments live in a folder next to the active data file, so the file and its
    // photos travel together if the file is moved, copied, or synced elsewhere.
    public void SetDataFilePath(string dataFilePath) => _rootFolder = ComputeRoot(dataFilePath);

    private static string ComputeRoot(string dataFilePath)
        => Path.Combine(Path.GetDirectoryName(dataFilePath) ?? ".", "Attachments");

    public string GetFolder(Guid taskId)
    {
        var folder = Path.Combine(_rootFolder, taskId.ToString());
        Directory.CreateDirectory(folder);
        return folder;
    }

    public string CopyFile(Guid taskId, string sourcePath)
    {
        var folder = GetFolder(taskId);
        var destPath = UniquePath(folder, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destPath, overwrite: false);
        return destPath;
    }

    public string SaveBytes(Guid taskId, byte[] bytes, string fileNameHint)
    {
        var folder = GetFolder(taskId);
        var destPath = UniquePath(folder, fileNameHint);
        File.WriteAllBytes(destPath, bytes);
        return destPath;
    }

    public void DeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore: file may be in use, already gone, or access is denied - the caller's own
            // in-memory removal (e.g. dropping a block from Task.Body) should still go through.
        }
    }

    // A link/file block's path is loaded straight from the .tasky JSON - before ever handing one
    // to the shell (Process.Start with UseShellExecute), confirm it actually lives inside this
    // file's own Attachments folder (the only place CopyFile/SaveBytes ever write to). Refuses
    // anything else, so a hand-edited or restored-from-an-untrusted-backup file can't point this
    // at an arbitrary local executable.
    public bool IsUnderAttachmentsRoot(string path)
    {
        var root = Path.GetFullPath(_rootFolder) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string UniquePath(string folder, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = Path.Combine(folder, fileName);
        var counter = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(folder, $"{name}_{counter}{ext}");
            counter++;
        }
        return candidate;
    }
}
