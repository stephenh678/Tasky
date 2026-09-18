using System;
using System.IO;

namespace TodoApp.Services;

/// <summary>
/// The per-user folders Tasky reads and writes by default: settings and the Drive token cache
/// under %AppData%\Tasky, the default data file and debug.log under Documents\Tasky.
///
/// One place rather than each service calling Environment.GetFolderPath itself, because those
/// scattered calls are what let a unit test that merely constructed a MainViewModel load the
/// developer's REAL settings.json, follow its LastFilePath to the real Tasky.tasky, and autosave
/// test fixtures over it. TodoApp.Tests redirects both roots to a temp folder from a module
/// initializer (TestEnvironment.cs), before any of these statics are first read - so no test,
/// present or future, can reach live user data no matter what it constructs.
/// </summary>
public static class TaskyPaths
{
    private static string? _appDataRootOverride;
    private static string? _documentsRootOverride;

    public static string AppDataRoot => _appDataRootOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tasky");

    public static string DocumentsRoot => _documentsRootOverride
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tasky");

    public static string SettingsFilePath => Path.Combine(AppDataRoot, "settings.json");
    public static string GoogleDriveTokenDirectory => Path.Combine(AppDataRoot, "GoogleDriveToken");
    public static string DefaultDataFilePath => Path.Combine(DocumentsRoot, "Tasky.tasky");
    public static string DebugLogFilePath => Path.Combine(DocumentsRoot, "debug.log");

    // Test-only. Internal (InternalsVisibleTo TodoApp.Tests) so production code can't repoint
    // where user data lives by accident.
    internal static void RedirectForTests(string appDataRoot, string documentsRoot)
    {
        _appDataRootOverride = appDataRoot;
        _documentsRootOverride = documentsRoot;
    }
}
