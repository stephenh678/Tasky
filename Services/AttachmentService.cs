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
        catch (IOException)
        {
            // Ignore: file may be in use or already gone.
        }
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
