using System.IO;
using System.Runtime.CompilerServices;
using TodoApp.Services;

namespace TodoApp.Tests;

// Runs once when the test assembly loads, before any test (and before AppLogger's or any other
// static initializer can read a path): points every default per-user location at a throwaway
// temp folder. Two MainViewModel tests used to construct the ViewModel against the real
// %AppData%\Tasky\settings.json, follow its LastFilePath to the developer's real Tasky.tasky, and
// autosave three fixture tasks over it on every `dotnet test`. Redirecting the roots here - rather
// than fixing just those two tests - means a future test can't reintroduce that by accident.
internal static class TestEnvironment
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), $"TaskyTests_Env_{Guid.NewGuid():N}");

    [ModuleInitializer]
    internal static void Initialize()
    {
        var appData = Path.Combine(Root, "AppData");
        var documents = Path.Combine(Root, "Documents");
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(documents);
        TaskyPaths.RedirectForTests(appData, documents);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        };
    }
}

// A MainViewModel with its own settings.json and its own data file, under a folder the caller
// owns - nothing on disk is shared with any other test, so these are safe to run in parallel.
internal static class TestViewModels
{
    public static TodoApp.ViewModels.MainViewModel Create(string directory, string dataFileName = "Tasky.tasky")
    {
        Directory.CreateDirectory(directory);
        return new TodoApp.ViewModels.MainViewModel(
            new SettingsStore(Path.Combine(directory, "settings.json")),
            Path.Combine(directory, dataFileName));
    }

    public static string NewDirectory() => Path.Combine(TestEnvironment.Root, "vm", Guid.NewGuid().ToString("N"));
}

public class TestEnvironmentTests
{
    // The guard for the guard: if the redirect above ever stops applying, this fails loudly
    // instead of the suite quietly going back to writing into the real profile.
    [Fact]
    public void DefaultPaths_AreRedirectedAwayFromTheRealUserProfile()
    {
        var realDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var realAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.StartsWith(TestEnvironment.Root, TaskyPaths.DefaultDataFilePath);
        Assert.StartsWith(TestEnvironment.Root, TaskyPaths.SettingsFilePath);
        Assert.StartsWith(TestEnvironment.Root, TaskyPaths.DebugLogFilePath);
        Assert.StartsWith(TestEnvironment.Root, TodoStore.GetDefaultDataFilePath());
        Assert.DoesNotContain(realDocuments, TaskyPaths.DefaultDataFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(realAppData, TaskyPaths.SettingsFilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MainViewModel_DefaultConstruction_OpensTheRedirectedDataFile()
    {
        var vm = new TodoApp.ViewModels.MainViewModel();
        Assert.StartsWith(TestEnvironment.Root, vm.CurrentFilePath);
    }
}
