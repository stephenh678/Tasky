using System;
using System.Threading.Tasks;
using TodoApp;
using Xunit;

namespace TodoApp.Tests;

public class AsyncRelayCommandTests
{
    [Fact]
    public async Task ExecuteAsync_ExecutesTask_AndTogglesIsExecuting()
    {
        var executed = false;
        var wasExecutingDuringRun = false;
        AsyncRelayCommand? command = null;

        command = new AsyncRelayCommand(async () =>
        {
            wasExecutingDuringRun = command!.IsExecuting;
            await Task.Delay(20);
            executed = true;
        });

        Assert.False(command.IsExecuting);
        Assert.True(command.CanExecute(null));

        await command.ExecuteAsync(null);

        Assert.True(executed);
        Assert.True(wasExecutingDuringRun);
        Assert.False(command.IsExecuting);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task CanExecute_ReturnsFalse_WhileExecutingToPreventReentrancy()
    {
        var tcs = new TaskCompletionSource<bool>();
        var command = new AsyncRelayCommand(async () =>
        {
            await tcs.Task;
        });

        Assert.True(command.CanExecute(null));

        var runTask = command.ExecuteAsync(null);

        // While running, CanExecute must be false
        Assert.True(command.IsExecuting);
        Assert.False(command.CanExecute(null));

        // Release the task
        tcs.SetResult(true);
        await runTask;

        Assert.False(command.IsExecuting);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public void CanExecute_RespectsCustomPredicate()
    {
        var allow = false;
        var command = new AsyncRelayCommand(async () => await Task.CompletedTask, () => allow);

        Assert.False(command.CanExecute(null));

        allow = true;
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task ExecuteAsync_CapturesException_AndInvokesOnExceptionHandler()
    {
        Exception? caughtException = null;
        var expectedEx = new InvalidOperationException("Test async error");

        var command = new AsyncRelayCommand(
            async () =>
            {
                await Task.Yield();
                throw expectedEx;
            },
            onException: ex => caughtException = ex);

        await command.ExecuteAsync(null);

        Assert.Same(expectedEx, caughtException);
        Assert.Same(expectedEx, command.ExecutionException);
        Assert.False(command.IsExecuting);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task ExecuteAsync_WithRethrowTrue_ThrowsException()
    {
        var expectedEx = new ApplicationException("Rethrow test");
        var command = new AsyncRelayCommand(async () =>
        {
            await Task.Yield();
            throw expectedEx;
        }, onException: _ => { });

        var thrown = await Assert.ThrowsAsync<ApplicationException>(async () =>
        {
            await command.ExecuteAsync(null, rethrow: true);
        });

        Assert.Same(expectedEx, thrown);
    }

    [Fact]
    public void SynchronousExecute_CatchesException_WithoutCrashing()
    {
        Exception? caught = null;
        var command = new AsyncRelayCommand(
            async () =>
            {
                await Task.Yield();
                throw new Exception("Synchronous execution error");
            },
            onException: ex => caught = ex);

        // Invoking ICommand.Execute synchronously should not throw an unhandled exception
        command.Execute(null);

        // Wait briefly for background async to complete
        var waitStart = DateTime.UtcNow;
        while (caught == null && DateTime.UtcNow - waitStart < TimeSpan.FromSeconds(1))
        {
            System.Threading.Thread.Sleep(10);
        }

        Assert.NotNull(caught);
        Assert.Equal("Synchronous execution error", caught.Message);
    }
}
