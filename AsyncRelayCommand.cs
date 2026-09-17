using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using TodoApp.Services;

namespace TodoApp;

/// <summary>
/// An asynchronous implementation of ICommand that prevents re-entrancy during execution,
/// captures exceptions without crashing the dispatcher synchronization context, and raises
/// CanExecuteChanged notifications when execution state transitions.
/// </summary>
public class AsyncRelayCommand : RelayCommand
{
    private readonly Func<object?, Task> _asyncExecute;
    private readonly Predicate<object?>? _canExecute;
    private readonly Action<Exception>? _onException;
    private bool _isExecuting;

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (_isExecuting != value)
            {
                _isExecuting = value;
                RaiseCanExecuteChanged();
            }
        }
    }

    public Exception? ExecutionException { get; private set; }

    public AsyncRelayCommand(Func<object?, Task> asyncExecute, Predicate<object?>? canExecute = null, Action<Exception>? onException = null)
        : base(_ => { }, canExecute)
    {
        _asyncExecute = asyncExecute ?? throw new ArgumentNullException(nameof(asyncExecute));
        _canExecute = canExecute;
        _onException = onException;
    }

    public AsyncRelayCommand(Func<Task> asyncExecute, Func<bool>? canExecute = null, Action<Exception>? onException = null)
        : this(_ => asyncExecute(), canExecute != null ? _ => canExecute() : null, onException)
    {
    }

    public override bool CanExecute(object? parameter)
    {
        if (_isExecuting) return false;
        return _canExecute?.Invoke(parameter) ?? true;
    }

    public override async void Execute(object? parameter)
    {
        await ExecuteAsync(parameter);
    }

    public async Task ExecuteAsync(object? parameter, bool rethrow = false)
    {
        if (!CanExecute(parameter)) return;

        ExecutionException = null;
        IsExecuting = true;
        try
        {
            await _asyncExecute(parameter);
        }
        catch (Exception ex)
        {
            ExecutionException = ex;
            AppLogger.Error("AsyncRelayCommand", "Unhandled exception during command execution", ex);
            if (_onException != null)
            {
                _onException(ex);
            }
            else
            {
                ShowErrorDialog(ex);
            }

            if (rethrow)
                throw;
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public void RaiseCanExecuteChanged()
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(CommandManager.InvalidateRequerySuggested);
        }
        else
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static void ShowErrorDialog(Exception ex)
    {
        if (Application.Current != null)
        {
            try
            {
                ThemedMessageBox.Show(
                    $"Tasky encountered an error:\n\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // Fallback in headless test or background thread scenarios
            }
        }
    }
}
