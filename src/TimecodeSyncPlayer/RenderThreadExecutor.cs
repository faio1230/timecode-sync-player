using System.Collections.Concurrent;
using System.Threading;

namespace TimecodeSyncPlayer;

internal sealed class RenderThreadExecutor : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private int _disposed;

    public RenderThreadExecutor()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "TimecodeSyncPlayer.Render",
        };
        _thread.Start();
    }

    public Task InvokeAsync(Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return InvokeAsync(() =>
        {
            operation();
            return true;
        });
    }

    public Task<T> InvokeAsync<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try
                {
                    completion.SetResult(operation());
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });
        }
        catch (InvalidOperationException)
        {
            completion.SetException(new ObjectDisposedException(nameof(RenderThreadExecutor)));
        }

        return completion.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _queue.CompleteAdding();
        if (Environment.CurrentManagedThreadId != _thread.ManagedThreadId)
            _thread.Join();
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (Action operation in _queue.GetConsumingEnumerable())
            operation();
    }
}
