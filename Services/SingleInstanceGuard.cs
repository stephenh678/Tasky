using System;
using System.Threading;

namespace TodoApp.Services;

/// <summary>
/// Keeps Tasky to one running copy per Windows session. With Close to Tray on (the default), the
/// main window is usually hidden, so launching Tasky again from the Start Menu used to start a
/// SECOND process: two tray icons, two global-hotkey registrations, and - the part that matters -
/// two autosave loops each rewriting the whole .tasky file from their own in-memory copy, so
/// whichever saved last silently discarded the other's edits.
///
/// The first instance owns a named mutex and listens on a named event; a later launch that can't
/// get the mutex sets the event (which makes the first instance show its window) and exits.
/// "Local\" scopes both to the session, so two different signed-in users each still get their own.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\Tasky.SingleInstance.8F1C5A2E-4B3D-4C7A-9E6F-2A8D0B4E7C15";
    private const string ShowEventName = @"Local\Tasky.ShowRequested.8F1C5A2E-4B3D-4C7A-9E6F-2A8D0B4E7C15";

    // A relaunch can land while the previous copy is still finishing its exit-time save/sync
    // (the in-app updater, or a quick Exit-then-reopen). Waiting briefly for it to let go starts
    // this copy normally instead of only signalling a process that's about to disappear.
    private static readonly TimeSpan ExitingInstanceGrace = TimeSpan.FromSeconds(5);

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _showRegistration;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle showEvent, Action onShowRequested)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>
    /// Returns the guard if this process is now THE instance (dispose it on exit), or null if
    /// another copy already is - in which case that copy has been asked to show itself and the
    /// caller should exit without creating any window. onShowRequested runs on a thread-pool
    /// thread; marshal to the UI thread inside it. Must be called from the thread that will later
    /// dispose the guard (a Mutex is thread-affine) - i.e. the UI thread.
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(Action onShowRequested)
        => TryAcquire(onShowRequested, nameSuffix: "", ExitingInstanceGrace);

    // Tests pass a unique suffix (so they never contend with a real running Tasky, or each other)
    // and a short grace period.
    internal static SingleInstanceGuard? TryAcquire(Action onShowRequested, string nameSuffix, TimeSpan exitingInstanceGrace)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName + nameSuffix);
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName + nameSuffix);

        var owned = TryOwn(mutex, TimeSpan.Zero);
        if (!owned)
        {
            // Ask first, wait second: a healthy running copy shows its window immediately, and
            // only this invisible process pays for the grace period.
            showEvent.Set();
            owned = TryOwn(mutex, exitingInstanceGrace);
            // Took over from a copy that exited without consuming the signal - don't let it fire
            // at ourselves the moment we start listening.
            if (owned) showEvent.Reset();
        }

        if (!owned)
        {
            showEvent.Dispose();
            mutex.Dispose();
            return null;
        }

        return new SingleInstanceGuard(mutex, showEvent, onShowRequested);
    }

    private static bool TryOwn(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner crashed without releasing it - the mutex is ours now.
            return true;
        }
    }

    public void Dispose()
    {
        _showRegistration.Unregister(null);
        _showEvent.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned on this thread */ }
        _mutex.Dispose();
    }
}
