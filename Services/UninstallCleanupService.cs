using System;
using System.IO;
using Microsoft.Win32;

namespace TodoApp.Services;

/// <summary>
/// Removes the per-user state Tasky leaves outside its own install folder. Invoked as
/// "Tasky.exe --uninstall-cleanup [--remove-data]" from the Inno Setup uninstaller's
/// [UninstallRun] step, while Tasky.exe still exists and before Inno deletes the program files.
///
/// Why the app does this rather than the installer: every one of these locations is defined by a
/// specific piece of Tasky (SettingsStore, UpdateService, StartupService, ToastNotificationService),
/// so keeping the knowledge here means adding a new one can't silently go unhandled the way it
/// would in a separate script that has to be remembered and updated in lockstep. It also runs as
/// the real user by construction, which the previous PowerShell uninstaller got wrong whenever
/// UAC collected a different administrator's credentials.
/// </summary>
public static class UninstallCleanupService
{
    /// <param name="removeTaskData">
    /// Also delete Documents\Tasky (the .tasky files, backups and attachments). The uninstaller
    /// asks the user; the default is to keep it, since it's the only genuinely irreplaceable thing
    /// here - everything else is a cache or a preference.
    /// </param>
    public static void CleanUp(bool removeTaskData)
    {
        // Ordered so that anything that could re-create a folder runs before that folder is
        // deleted - notification cleanup in particular touches the registry, not the filesystem,
        // but keeping it first matches the order the old uninstaller established.
        TryStep("notification registration", ToastNotificationService.Uninstall);
        TryStep("startup registration", RemoveStartupRegistration);
        TryStep("settings and Google Drive sign-in cache",
            () => DeleteDirectory(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tasky")));
        TryStep("update staging cache",
            () => DeleteDirectory(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tasky")));

        if (removeTaskData)
        {
            // Last, and nothing may log afterwards: AppLogger writes into this very folder, so a
            // single log line after this point silently recreates it containing nothing but
            // debug.log. The caller exits the process immediately rather than returning through
            // OnExit's flush for the same reason.
            TryStep("task data", () => DeleteDirectory(DataFolder));
        }
    }

    private static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tasky");

    /// <summary>
    /// The one thing an uninstall can't clean up for the user, surfaced so the installer can say
    /// so: "Save Data File As..." lets a .tasky file live anywhere, and only the most recent path
    /// is remembered. Returns null when the current data file is inside the folder Tasky owns (so
    /// removing that folder covers it) or when there's nothing recorded.
    /// </summary>
    public static string? GetExternalDataFilePath()
    {
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tasky", "settings.json");
            if (!File.Exists(settingsPath)) return null;

            var settings = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath));
            var lastFile = settings?.LastFilePath;
            if (string.IsNullOrWhiteSpace(lastFile) || !File.Exists(lastFile)) return null;

            var directory = Path.GetDirectoryName(Path.GetFullPath(lastFile));
            if (string.IsNullOrEmpty(directory)) return null;

            // Compare with a trailing separator on both sides, or a sibling folder like
            // "Documents\Tasky2" counts as living inside "Documents\Tasky".
            var owned = DataFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(owned, StringComparison.OrdinalIgnoreCase) ? null : lastFile;
        }
        catch (Exception)
        {
            // Unreadable or corrupt settings - nothing useful to report.
            return null;
        }
    }

    private static void RemoveStartupRegistration()
    {
        // StartupService owns this key; deleting the value is the same operation as toggling
        // "Start with Windows" off, and DeleteValue with throwOnMissingValue:false is a no-op when
        // the user never turned it on.
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.DeleteValue("Tasky", throwOnMissingValue: false);
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    // A cleanup pass must never fail the uninstall: whatever can't be removed here is a leftover
    // the user can delete by hand, whereas an exception would leave Inno reporting a failed
    // uninstall for a file that was already going to be gone anyway.
    private static void TryStep(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UninstallCleanup", $"Could not remove {what}: {ex.Message}");
        }
    }
}
