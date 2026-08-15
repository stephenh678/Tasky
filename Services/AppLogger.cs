using System;
using System.Diagnostics;
using System.IO;

namespace TodoApp.Services;

/// <summary>
/// Thread-safe verbose logger for Tasky diagnostic tracing and error tracking.
/// Logs are persisted to %USERPROFILE%\Documents\Tasky\debug.log and sent to Debug/Trace listeners.
/// </summary>
public static class AppLogger
{
    private static readonly object _lock = new();
    private static readonly string _logFilePath;

    static AppLogger()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tasky");
            Directory.CreateDirectory(dir);
            _logFilePath = Path.Combine(dir, "debug.log");

            WriteRaw(Environment.NewLine +
                     "================================================================================" + Environment.NewLine +
                     $"TASKY DEBUG LOG STARTED: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | Process ID: {Environment.ProcessId}" + Environment.NewLine +
                     $"OS: {Environment.OSVersion} | 64-Bit: {Environment.Is64BitProcess} | .NET: {Environment.Version}" + Environment.NewLine +
                     "================================================================================" + Environment.NewLine);
        }
        catch
        {
            _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
        }
    }

    public static string LogFilePath => _logFilePath;

    public static bool IsVerbose { get; set; } = false;

    public static void Debug(string category, string message)
    {
        if (IsVerbose)
            Log("DEBUG", category, message);
    }

    public static void Info(string category, string message) => Log("INFO ", category, message);

    public static void Warn(string category, string message) => Log("WARN ", category, message);

    public static void Error(string category, string message, Exception? ex = null)
    {
        var text = ex is not null ? $"{message}{Environment.NewLine}Exception: {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}StackTrace: {ex.StackTrace}" : message;
        Log("ERROR", category, text);
    }

    private static void Log(string level, string category, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{category,-16}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        Trace.WriteLine(line);
        WriteRaw(line + Environment.NewLine);
    }

    private static void WriteRaw(string text)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logFilePath, text);
            }
            catch
            {
            }
        }
    }

    public static void OpenLogFile()
    {
        try
        {
            if (File.Exists(_logFilePath))
            {
                Process.Start(new ProcessStartInfo(_logFilePath) { UseShellExecute = true });
            }
            else
            {
                ThemedMessageBox.Show($"Log file not created yet:\n{_logFilePath}", "Debug Log",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"Unable to open log file:\n{ex.Message}", "Debug Log",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    public static void ClearLogFile()
    {
        lock (_lock)
        {
            try
            {
                File.WriteAllText(_logFilePath,
                    "================================================================================" + Environment.NewLine +
                    $"TASKY DEBUG LOG CLEARED: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | Process ID: {Environment.ProcessId}" + Environment.NewLine +
                    $"OS: {Environment.OSVersion} | 64-Bit: {Environment.Is64BitProcess} | .NET: {Environment.Version}" + Environment.NewLine +
                    "================================================================================" + Environment.NewLine);
            }
            catch { }
        }
    }
}
