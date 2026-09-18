using System;
using System.IO;
using System.Text.Json;
using TodoApp.Services;
using Xunit;

namespace TodoApp.Tests;

/// <summary>
/// UninstallCleanupService recursively deletes directories on a real user's profile, so the thing
/// worth testing is precisely which ones it touches and which it leaves alone. Every test drives it
/// through injected temp paths - the production defaults are never used here, and the HKCU Run key
/// is stubbed out, so running the suite can't disturb a machine's actual Tasky install.
/// </summary>
public class UninstallCleanupServiceTests : IDisposable
{
    private readonly string _root;
    private readonly UninstallCleanupPaths _paths;

    public UninstallCleanupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tasky-cleanup-tests-" + Guid.NewGuid().ToString("N"));
        _paths = new UninstallCleanupPaths(
            Path.Combine(_root, "settings"),
            Path.Combine(_root, "updatecache"),
            Path.Combine(_root, "data"));
        foreach (var dir in new[] { _paths.SettingsFolder, _paths.UpdateCacheFolder, _paths.DataFolder })
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "marker.txt"), "x");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // Deliberately does nothing: the real one edits HKCU, which a test must never do.
    private static void NoStartupChange() { }

    [Fact]
    public void CleanUp_WithoutRemoveData_KeepsTaskDataAndClearsTheRest()
    {
        var result = UninstallCleanupService.CleanUp(removeTaskData: false, _paths, NoStartupChange);

        Assert.False(Directory.Exists(_paths.SettingsFolder));
        Assert.False(Directory.Exists(_paths.UpdateCacheFolder));
        // The default, and the whole point of the uninstaller asking: task data is the only
        // irreplaceable thing here.
        Assert.True(Directory.Exists(_paths.DataFolder));
        Assert.Empty(result.Failures);
    }

    [Fact]
    public void CleanUp_WithRemoveData_ClearsTaskDataToo()
    {
        UninstallCleanupService.CleanUp(removeTaskData: true, _paths, NoStartupChange);

        Assert.False(Directory.Exists(_paths.DataFolder));
    }

    [Fact]
    public void CleanUp_WithNothingInstalled_ReportsNoFailures()
    {
        Directory.Delete(_root, recursive: true);

        var result = UninstallCleanupService.CleanUp(removeTaskData: true, _paths, NoStartupChange);

        Assert.Empty(result.Failures);
    }

    // A cleanup pass must never fail the uninstall - whatever it can't remove is a leftover the
    // user can delete by hand, but the reason has to reach the caller or it reaches nobody (the
    // process exits before any log is flushed).
    [Fact]
    public void CleanUp_WhenAStepThrows_CollectsItAndKeepsGoing()
    {
        var result = UninstallCleanupService.CleanUp(
            removeTaskData: false, _paths,
            removeStartupRegistration: () => throw new InvalidOperationException("registry locked"));

        Assert.Contains(result.Failures, f => f.Contains("startup registration") && f.Contains("registry locked"));
        // The later steps still ran.
        Assert.False(Directory.Exists(_paths.SettingsFolder));
        Assert.False(Directory.Exists(_paths.UpdateCacheFolder));
    }

    // Captured before the settings folder it's read from is deleted.
    [Fact]
    public void CleanUp_ReportsAnExternalDataFileEvenThoughSettingsAreRemoved()
    {
        var external = Path.Combine(_root, "elsewhere", "work.tasky");
        Directory.CreateDirectory(Path.GetDirectoryName(external)!);
        File.WriteAllText(external, "{}");
        WriteSettings(external);

        var result = UninstallCleanupService.CleanUp(removeTaskData: true, _paths, NoStartupChange);

        Assert.Equal(external, result.ExternalDataFilePath);
        Assert.False(Directory.Exists(_paths.SettingsFolder));
        // Never touched: it's outside the folder Tasky owns.
        Assert.True(File.Exists(external));
    }

    [Fact]
    public void GetExternalDataFilePath_InsideTheDataFolder_ReportsNothing()
    {
        var inside = Path.Combine(_paths.DataFolder, "tasks.tasky");
        File.WriteAllText(inside, "{}");
        WriteSettings(inside);

        Assert.Null(UninstallCleanupService.GetExternalDataFilePath(_paths));
    }

    [Fact]
    public void GetExternalDataFilePath_InASubfolderOfTheDataFolder_ReportsNothing()
    {
        var nested = Path.Combine(_paths.DataFolder, "Archive", "old.tasky");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, "{}");
        WriteSettings(nested);

        Assert.Null(UninstallCleanupService.GetExternalDataFilePath(_paths));
    }

    // The bug the PowerShell uninstaller shipped with: "...\data2" starts with "...\data", so a
    // sibling folder counted as living inside the one about to be deleted and its owner was never
    // warned. The trailing-separator comparison is what stops that.
    [Fact]
    public void GetExternalDataFilePath_InASiblingFolderWithASharedPrefix_IsReportedAsExternal()
    {
        var sibling = _paths.DataFolder + "2";
        Directory.CreateDirectory(sibling);
        var file = Path.Combine(sibling, "tasks.tasky");
        File.WriteAllText(file, "{}");
        WriteSettings(file);

        Assert.Equal(file, UninstallCleanupService.GetExternalDataFilePath(_paths));
    }

    [Fact]
    public void GetExternalDataFilePath_WithNoSettingsFile_ReportsNothing()
    {
        Assert.Null(UninstallCleanupService.GetExternalDataFilePath(_paths));
    }

    [Fact]
    public void GetExternalDataFilePath_WithCorruptSettings_ReportsNothing()
    {
        File.WriteAllText(Path.Combine(_paths.SettingsFolder, "settings.json"), "{ not json");

        Assert.Null(UninstallCleanupService.GetExternalDataFilePath(_paths));
    }

    // A path recorded in settings that no longer exists is not something to warn about - the file
    // is already gone.
    [Fact]
    public void GetExternalDataFilePath_WhenTheRecordedFileIsMissing_ReportsNothing()
    {
        WriteSettings(Path.Combine(_root, "deleted", "gone.tasky"));

        Assert.Null(UninstallCleanupService.GetExternalDataFilePath(_paths));
    }

    private void WriteSettings(string lastFilePath)
    {
        Directory.CreateDirectory(_paths.SettingsFolder);
        File.WriteAllText(
            Path.Combine(_paths.SettingsFolder, "settings.json"),
            JsonSerializer.Serialize(new { LastFilePath = lastFilePath }));
    }
}
