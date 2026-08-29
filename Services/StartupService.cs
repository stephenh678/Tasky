using Microsoft.Win32;

namespace TodoApp.Services;

/// <summary>
/// ROADMAP.md #135: "Start with Windows" toggle. Desktop-only by nature - a browser tab has no
/// equivalent to launching at OS boot, so Tasky Web/mobile simply don't offer this setting rather
/// than faking parity with something that can't exist there.
///
/// Deliberately has no backing field in Settings.json - the per-user Run key IS the source of
/// truth, read fresh on every check. A cached bool could drift from reality (e.g. the user removes
/// it by hand via Task Manager's Startup tab) and silently lie to the Settings checkbox.
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Tasky";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            // Quoted so a path containing spaces (Program Files, a username with a space, ...)
            // isn't parsed as multiple arguments when Windows launches this at logon.
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
