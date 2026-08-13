using System;
using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace TodoApp;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    // Without this, ANY unhandled exception on the UI thread - a stray null ref in an event
    // handler, a reentrant WPF timing issue like the one reported closing Quick Add - takes down
    // the entire app instantly, with whatever was unsaved lost. Surface it and keep running
    // instead; autosave means very little is actually at risk by continuing.
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        ThemedMessageBox.Show(
            $"Tasky ran into a problem and may be unstable:\n\n{e.Exception.Message}\n\nYour work is being autosaved as you go, but you may want to restart Tasky soon.",
            "Unexpected Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    // The dialog only shows the short Message, deliberately - a full stack trace isn't useful to
    // read on-screen mid-task. The full exception (including stack trace, which pinpoints the
    // exact call site) goes to this log instead, so it can be read back later without needing to
    // reproduce blind a second time. Internal (not private) so other last-resort error paths in
    // the app (e.g. MainWindow's Closing handler) can reuse it instead of duplicating logging.
    //
    // Catches Exception broadly, not just IOException: this is itself the app's last line of
    // defense, so if writing the log fails for a reason that ISN'T IOException (e.g.
    // UnauthorizedAccessException on a locked-down/managed machine - which does NOT derive from
    // IOException), that would otherwise throw back out of the very unhandled-exception handler
    // that's supposed to prevent a hard crash, defeating the whole point of having it.
    internal static void LogException(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tasky");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
        }
    }
}
