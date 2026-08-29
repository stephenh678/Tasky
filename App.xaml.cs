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

    // Uninstall-Tasky.ps1 launches "Tasky.exe --cleanup-notifications" (and waits for it to exit)
    // right before deleting the app's files, so the registry-based toast notification
    // registration ToastNotificationService.Initialize() sets up on first run doesn't get left
    // behind. Deliberately checked before base.OnStartup - StartupUri would otherwise create and
    // show MainWindow (and its tray icon) for what's meant to be a silent, instant cleanup pass.
    protected override void OnStartup(StartupEventArgs e)
    {
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
