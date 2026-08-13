using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TodoApp.Services;

public static class ThemeService
{
    public static bool IsDark { get; private set; }

    public static void Apply(string themeName)
    {
        IsDark = themeName == "Dark";

        var dict = new ResourceDictionary
        {
            Source = new Uri($"Themes/{themeName}Theme.xaml", UriKind.Relative)
        };

        var app = Application.Current;
        var existing = app.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source is not null && d.Source.OriginalString.Contains("Theme.xaml"));

        if (existing is not null)
            app.Resources.MergedDictionaries.Remove(existing);

        app.Resources.MergedDictionaries.Insert(0, dict);

        // Tried .NET 9's ThemeMode (Fluent theme, auto dark-mode-aware controls) here - reverted.
        // It's marked WPF0001 ("evaluation purposes only") for a reason: it visibly broke parts
        // of the app's existing custom styling. Sticking with the hand-rolled ControlStyles.xaml
        // approach.
    }

    // A window's title bar is drawn by the OS (DWM), entirely outside WPF's content area, so no
    // XAML brush ever reaches it. This was previously wired up only inside MainWindow, which is
    // why every OTHER window (dialogs, the quick-add popup) kept a light title bar even in dark
    // mode - it needs to run per-window, not once for the app.
    //
    // Safe to call any time: before the window's HWND exists yet (e.g. from its constructor) it
    // defers to SourceInitialized; called again later (e.g. re-applying after the user toggles
    // the in-app theme on an already-open window) it applies immediately.
    public static void ApplyTitleBar(Window window)
    {
        if (PresentationSource.FromVisual(window) is not null)
            SetImmersiveDarkMode(window);
        else
            window.SourceInitialized += (_, _) => SetImmersiveDarkMode(window);
    }

    private static void SetImmersiveDarkMode(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var value = IsDark ? 1 : 0;
        try { DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)); }
        catch (EntryPointNotFoundException) { }
        catch (DllNotFoundException) { }
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
