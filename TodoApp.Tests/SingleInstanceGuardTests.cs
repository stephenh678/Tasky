using TodoApp.Services;

namespace TodoApp.Tests;

// A Mutex is owned by a THREAD, and re-entrant on it - so "a second process" has to be played by
// a second thread here, or the second TryAcquire would just succeed.
public class SingleInstanceGuardTests
{
    private static readonly TimeSpan ShortGrace = TimeSpan.FromMilliseconds(150);

    private static T OnAnotherThread<T>(Func<T> work)
    {
        T result = default!;
        var thread = new Thread(() => result = work());
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void SecondLaunch_IsTurnedAway_AndTheFirstIsAskedToShowItself()
    {
        var suffix = "." + Guid.NewGuid().ToString("N");
        using var shown = new ManualResetEventSlim();
        using var first = SingleInstanceGuard.TryAcquire(shown.Set, suffix, ShortGrace);
        Assert.NotNull(first);

        var secondGotIt = OnAnotherThread(() =>
        {
            using var second = SingleInstanceGuard.TryAcquire(() => { }, suffix, ShortGrace);
            return second is not null;
        });

        Assert.False(secondGotIt);
        Assert.True(shown.Wait(TimeSpan.FromSeconds(5)), "the running instance was never asked to show its window");
    }

    [Fact]
    public async Task LaunchDuringThePreviousInstancesExit_WaitsForItAndTakesOver()
    {
        var suffix = "." + Guid.NewGuid().ToString("N");
        using var release = new ManualResetEventSlim();
        using var firstReady = new ManualResetEventSlim();

        // The "exiting" instance: holds the lock briefly, then lets go - on the thread that took it.
        var exiting = new Thread(() =>
        {
            var guard = SingleInstanceGuard.TryAcquire(() => { }, suffix, ShortGrace);
            firstReady.Set();
            release.Wait();
            guard!.Dispose();
        });
        exiting.Start();
        firstReady.Wait();

        var takeover = Task.Run(() => OnAnotherThread(() =>
        {
            using var next = SingleInstanceGuard.TryAcquire(() => { }, suffix, TimeSpan.FromSeconds(10));
            return next is not null;
        }));
        Thread.Sleep(100);
        release.Set();
        exiting.Join();

        Assert.True(await takeover);
    }

    [Fact]
    public void AfterTheFirstExits_ANewLaunchStartsNormally()
    {
        var suffix = "." + Guid.NewGuid().ToString("N");
        OnAnotherThread(() =>
        {
            using var first = SingleInstanceGuard.TryAcquire(() => { }, suffix, ShortGrace);
            return first is not null;
        });

        using var second = SingleInstanceGuard.TryAcquire(() => { }, suffix, ShortGrace);
        Assert.NotNull(second);
    }
}
