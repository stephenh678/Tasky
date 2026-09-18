using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace TodoApp.Services;

/// <summary>
/// The locations <see cref="UninstallCleanupService"/> operates on. Defaults resolve to the real
/// per-user folders; tests substitute temp directories so the deletion logic can be exercised
/// without a machine's actual profile being at stake.
/// </summary>
public sealed record UninstallCleanupPaths(string SettingsFolder, string UpdateCacheFolder, string DataFolder)
{
    public static UninstallCleanupPaths Default { get; } = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tasky"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tasky"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tasky"));
}

/// <summary>What a cleanup pass did, so the caller can report failures somewhere durable.</summary>
public sealed record UninstallCleanupResult(IReadOnlyList<string> Failures, string? ExternalDataFilePath);

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
    /// <param name="paths">Overridden by tests; production passes null for the real profile.</param>
    /// <param name="removeStartupRegistration">
    /// Overridden by tests so they never touch the real HKCU Run key.
    /// </param>
    public static UninstallCleanupResult CleanUp(
        bool removeTaskData,
        UninstallCleanupPaths? paths = null,
        Action? removeStartupRegistration = null)
    {
        paths ??= UninstallCleanupPaths.Default;
        removeStartupRegistration ??= RemoveStartupRegistration;

        // Captured before anything is deleted: it's read out of the settings file that the very
        // next step removes.
        var externalFile = GetExternalDataFilePath(paths);

        var failures = new List<string>();
        void TryStep(string what, Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                // Collected rather than logged: AppLogger writes into the data folder this pass
                // may be deleting, and its queued writes are dropped when the caller exits. The
                // caller persists these somewhere that survives instead.
                failures.Add($"{what}: {ex.Message}");
            }
        }

        TryStep("notification registration", ToastNotificationService.Uninstall);
        TryStep("startup registration", removeStartupRegistration);
        TryStep("settings and Google Drive sign-in cache", () => DeleteDirectory(paths.SettingsFolder));
        TryStep("update staging cache", () => DeleteDirectory(paths.UpdateCacheFolder));

        if (removeTaskData)
        {
            // Last: AppLogger writes into this folder, so anything that logs after this point
            // recreates it containing nothing but debug.log.
            TryStep("task data", () => DeleteDirectory(paths.DataFolder));
        }

        return new UninstallCleanupResult(failures, externalFile);
    }

    /// <summary>
    /// The one thing an uninstall can't clean up for the user: "Save Data File As..." lets a
    /// .tasky file live anywhere, and only the most recent path is remembered. Returns null when
    /// the current data file is inside the folder Tasky owns (so removing that folder covers it)
    /// or when there's nothing recorded.
    /// </summary>
    public static string? GetExternalDataFilePath(UninstallCleanupPaths? paths = null)
    {
        paths ??= UninstallCleanupPaths.Default;
        try
        {
            var settingsPath = Path.Combine(paths.SettingsFolder, "settings.json");
            if (!File.Exists(settingsPath)) return null;

            var settings = System.Text.Json.JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath));
            var lastFile = settings?.LastFilePath;
            if (string.IsNullOrWhiteSpace(lastFile) || !File.Exists(lastFile)) return null;

            var directory = Path.GetDirectoryName(Path.GetFullPath(lastFile));
            if (string.IsNullOrEmpty(directory)) return null;

            // Compare with a trailing separator on both sides, or a sibling folder like
            // "Documents\Tasky2" counts as living inside "Documents\Tasky".
            var owned = paths.DataFolder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
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
}
