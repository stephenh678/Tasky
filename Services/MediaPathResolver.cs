using System.IO;

namespace TodoApp.Services;

// Single source of truth for where a data file's attachment media lives. Every media consumer
// (RichTextBoxBehavior's inline editor, MainViewModel's orphan cleanup, GoogleDriveService's
// sync) must resolve "Attachments"/"InlineImages" through here instead of computing its own
// path, so they can never disagree about where a given .tasky file's media actually is.
public static class MediaPathResolver
{
    private static string _dataFilePath = TodoStore.GetDefaultDataFilePath();

    // Tracks whichever .tasky file is currently open (see MainViewModel.LoadFile / SaveFileAsCommand).
    public static void SetDataFilePath(string dataFilePath) => _dataFilePath = dataFilePath;

    public static string AttachmentsDirectory => DirectoryFor(_dataFilePath, "Attachments");
    public static string InlineImagesDirectory => DirectoryFor(_dataFilePath, "InlineImages");

    // For call sites (Drive sync/download) that operate on a specific data file path rather than
    // necessarily the currently-open one.
    public static string DirectoryFor(string dataFilePath, string dirName)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataFilePath)) ?? ".", dirName);
}
