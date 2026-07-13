using System.Windows.Input;

namespace TimecodeSyncPlayer;

internal sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? _) => canExecute?.Invoke() ?? true;
    public void Execute(object? _) => execute();
    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() =>
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal sealed class AsyncRelayCommand(Func<CancellationToken, Task> execute) : ICommand
{
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public bool CanExecute(object? _) => !_isRunning;

    public async void Execute(object? _)
    {
        if (_isRunning) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(_cts.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Cancel() => _cts?.Cancel();
    public event EventHandler? CanExecuteChanged;
}
