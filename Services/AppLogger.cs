using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TodoApp.Services;

/// <summary>
/// Thread-safe verbose logger for Tasky diagnostic tracing and error tracking.
/// Logs are persisted to %USERPROFILE%\Documents\Tasky\debug.log and sent to Debug/Trace listeners.
/// Writes are queued through a channel and flushed by a single background consumer task (ROADMAP
/// #127) so callers - many of them on the UI thread - never block on disk I/O.
/// </summary>
public static class AppLogger
{
    private static readonly object _clearLock = new();
    private static readonly string _logFilePath;
    private static readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private static readonly Task _consumerTask;

    static AppLogger()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tasky");
            Directory.CreateDirectory(dir);
            _logFilePath = Path.Combine(dir, "debug.log");
        }
        catch
        {
            _logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug.log");
        }

        _consumerTask = Task.Run(ConsumeAsync);

        WriteRaw(Environment.NewLine +
                 "================================================================================" + Environment.NewLine +
                 $"TASKY DEBUG LOG STARTED: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | Process ID: {Environment.ProcessId}" + Environment.NewLine +
                 $"OS: {Environment.OSVersion} | 64-Bit: {Environment.Is64BitProcess} | .NET: {Environment.Version}" + Environment.NewLine +
                 "================================================================================" + Environment.NewLine);
    }

    private static async Task ConsumeAsync()
    {
        await foreach (var text in _channel.Reader.ReadAllAsync())
        {
            try
            {
                lock (_clearLock)
                {
                    File.AppendAllText(_logFilePath, text);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Completes the write queue and waits (briefly) for the background consumer to drain it, so
    /// log lines written right before shutdown (often the most useful - crash diagnostics) aren't
    /// silently lost. Called once from App.OnExit.
    /// </summary>
    public static void Flush()
    {
        _channel.Writer.TryComplete();
        try
        {
            _consumerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
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
        _channel.Writer.TryWrite(text);
    }

    public enum OpenLogFileResult { Opened, NotCreatedYet, Failed }

    // ROADMAP #127: used to show ThemedMessageBox dialogs itself - a Services-layer static
    // shouldn't own UI presentation. Now just reports the outcome; the caller (MainViewModel)
    // decides how to surface it.
    public static OpenLogFileResult OpenLogFile(out string? errorMessage)
    {
        errorMessage = null;
        try
        {
            if (!File.Exists(_logFilePath))
                return OpenLogFileResult.NotCreatedYet;

            Process.Start(new ProcessStartInfo(_logFilePath) { UseShellExecute = true });
            return OpenLogFileResult.Opened;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return OpenLogFileResult.Failed;
        }
    }

    public static void ClearLogFile()
    {
        lock (_clearLock)
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
