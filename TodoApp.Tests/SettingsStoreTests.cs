using System;
using System.IO;
using TodoApp.Services;

namespace TodoApp.Tests;

// Each test gets its own scratch settings.json path under the OS temp folder so these never touch
// the real %AppData%\Tasky\settings.json, and can run in parallel without colliding.
public class SettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _filePath;

    public SettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "TaskySettingsStoreTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        _filePath = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Load_NoFileYet_ReturnsDefaultsWithNoWarning()
    {
        var store = new SettingsStore(_filePath);

        var settings = store.Load();

        Assert.Equal("Light", settings.Theme);
        Assert.Null(store.LastLoadWarning);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsSettings()
    {
        var store = new SettingsStore(_filePath);
        var original = new Settings { Theme = "Dark", SidebarCollapsed = true, GoogleDriveAccountEmail = "me@example.com" };

        Assert.True(store.Save(original));
        var loaded = store.Load();

        Assert.Equal("Dark", loaded.Theme);
        Assert.True(loaded.SidebarCollapsed);
        Assert.Equal("me@example.com", loaded.GoogleDriveAccountEmail);
        Assert.Null(store.LastLoadWarning);
    }

    [Fact]
    public void SaveThenSaveAgain_OverwritesWithoutLeavingTempFile()
    {
        var store = new SettingsStore(_filePath);
        store.Save(new Settings { Theme = "Light" });

        store.Save(new Settings { Theme = "Dark" });

        Assert.Equal("Dark", store.Load().Theme);
        Assert.False(File.Exists(_filePath + ".tmp"));
    }

    [Fact]
    public void Save_GoogleDriveClientSecret_NeverWrittenAsPlaintext()
    {
        var store = new SettingsStore(_filePath);
        store.Save(new Settings { GoogleDriveClientSecret = "GOCSPX-example-secret" });

        var raw = File.ReadAllText(_filePath);

        Assert.DoesNotContain("GOCSPX-example-secret", raw);
    }

    [Fact]
    public void Load_GoogleDriveClientSecret_RoundTripsThroughEncryption()
    {
        var store = new SettingsStore(_filePath);
        store.Save(new Settings { GoogleDriveClientSecret = "GOCSPX-example-secret" });

        var loaded = store.Load();

        Assert.Equal("GOCSPX-example-secret", loaded.GoogleDriveClientSecret);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaultsInsteadOfThrowing()
    {
        File.WriteAllText(_filePath, "{ this is not valid json ");
        var store = new SettingsStore(_filePath);

        var settings = store.Load();

        Assert.Equal("Light", settings.Theme); // Settings' own default, confirms a fresh instance
    }

    [Fact]
    public void Load_CorruptJson_SetsLastLoadWarning()
    {
        File.WriteAllText(_filePath, "{ this is not valid json ");
        var store = new SettingsStore(_filePath);

        store.Load();

        Assert.NotNull(store.LastLoadWarning);
    }

    [Fact]
    public void Load_CorruptJson_BacksUpTheCorruptFileInsteadOfLosingIt()
    {
        const string corruptContent = "{ this is not valid json ";
        File.WriteAllText(_filePath, corruptContent);
        var store = new SettingsStore(_filePath);

        store.Load();

        var backups = Directory.GetFiles(_dir, "settings.json.corrupt-*");
        Assert.Single(backups);
        Assert.Equal(corruptContent, File.ReadAllText(backups[0]));
    }

    [Fact]
    public void Load_AfterRecoveringFromCorruption_NextLoadHasNoWarning()
    {
        File.WriteAllText(_filePath, "{ this is not valid json ");
        var store = new SettingsStore(_filePath);
        store.Load();
        Assert.NotNull(store.LastLoadWarning);

        // Simulates the app running normally after the one-time reset: a fresh save overwrites
        // the corrupt file, and loading it back shouldn't keep re-warning every launch.
        store.Save(new Settings { Theme = "Dark" });
        store.Load();

        Assert.Null(store.LastLoadWarning);
    }
}
