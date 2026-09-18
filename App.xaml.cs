using System;
using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using TodoApp.Services;

namespace TodoApp;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        AppLogger.Info("App", "Application instance created and initializing.");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                AppLogger.Error("AppDomain", "Fatal unhandled domain exception", ex);
        };
    }

    // The uninstaller (installer/Tasky.iss [UninstallRun]) runs
    // "Tasky.exe --uninstall-cleanup [--remove-data]" and waits for it, while Tasky.exe still
    // exists and before Inno deletes the program files. That removes the per-user state living
    // outside the install folder - see UninstallCleanupService for why the app owns this rather
    // than the installer.
    //
    // --cleanup-notifications is the narrower predecessor, kept because copies installed before
    // the Inno installer existed still shipped a PowerShell uninstaller that calls it by name.
    //
    // Both are checked before base.OnStartup: StartupUri would otherwise create and show
    // MainWindow (and its tray icon) for what's meant to be a silent, instant cleanup pass.
    protected override void OnStartup(StartupEventArgs e)
    {
        if (Array.IndexOf(e.Args, "--uninstall-cleanup") >= 0)
        {
            var removeData = Array.IndexOf(e.Args, "--remove-data") >= 0;
            // Flush BEFORE the cleanup when the data folder is going: AppLogger writes into
            // Documents\Tasky, so draining the queue afterwards would recreate that folder
            // containing nothing but debug.log - exactly the litter this pass exists to remove.
            // When the folder is being kept, flushing after is fine and keeps the record complete.
            if (removeData) AppLogger.Flush();
            UninstallCleanupService.CleanUp(removeData);
            if (!removeData) AppLogger.Flush();
            // Environment.Exit, not Shutdown(): Shutdown returns through OnExit, which flushes
            // AppLogger again and would undo the ordering above.
            Environment.Exit(0);
            return;
        }

        if (Array.IndexOf(e.Args, "--cleanup-notifications") >= 0)
        {
            try
            {
                ToastNotificationService.Uninstall();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("App", $"Notification cleanup failed: {ex.Message}");
            }
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    // ROADMAP #127: AppLogger now queues writes through a background consumer instead of writing
    // synchronously, so anything logged right before shutdown (crash diagnostics especially) needs
    // an explicit flush-and-wait here or it can be lost when the process exits mid-queue.
    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Flush();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Dispatcher", "Unhandled UI dispatcher exception", e.Exception);
        ThemedMessageBox.Show(
            $"Tasky ran into a problem and may be unstable:\n\n{e.Exception.Message}\n\nYour work is being autosaved as you go, but you may want to restart Tasky soon.",
            "Unexpected Problem", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    internal static void LogException(Exception ex)
    {
        AppLogger.Error("Exception", "Handled exception recorded", ex);
    }
}
