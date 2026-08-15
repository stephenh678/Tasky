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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Dispatcher", "Unhandled UI dispatcher exception", e.Exception);
        ThemedMessageBox.Show(
            $"Tasky ran into a problem and may be unstable:\n\n{e.Exception.Message}\n\nYour work is being autosaved as you go, but you may want to restart Tasky soon.",
            "Unexpected Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    internal static void LogException(Exception ex)
    {
        AppLogger.Error("Exception", "Handled exception recorded", ex);
    }
}
