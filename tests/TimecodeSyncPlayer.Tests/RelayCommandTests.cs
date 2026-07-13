using System.Windows.Input;
using FluentAssertions;
using TimecodeSyncPlayer;

namespace TimecodeSyncPlayer.Tests;

public class RelayCommandTests
{
    [Fact]
    public void Execute_InvokesAction()
    {
        int callCount = 0;
        var cmd = new RelayCommand(() => callCount++);

        cmd.Execute(null);

        callCount.Should().Be(1);
    }

    [Fact]
    public void CanExecute_DefaultsToTrue()
    {
        var cmd = new RelayCommand(() => { });

        cmd.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void CanExecute_ReturnsFalseWhenPredicateFalse()
    {
        var cmd = new RelayCommand(() => { }, () => false);

        cmd.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void RaiseCanExecuteChanged_FiresEvent()
    {
        var cmd = new RelayCommand(() => { });
        bool fired = false;
        cmd.CanExecuteChanged += (_, _) => fired = true;

        cmd.RaiseCanExecuteChanged();

        fired.Should().BeTrue();
    }
}

public class AsyncRelayCommandTests
{
    [Fact]
    public async Task Execute_RunsAndCompletesAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        var cmd = new AsyncRelayCommand(async ct =>
        {
            await Task.Yield();
            tcs.SetResult(true);
        });

        cmd.Execute(null);
        bool result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        result.Should().BeTrue();
    }

    [Fact]
    public void CanExecute_TrueInitially()
    {
        var cmd = new AsyncRelayCommand(_ => Task.CompletedTask);

        cmd.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_StopsRunningOperation()
    {
        bool cancelled = false;
        var lambdaStarted = new TaskCompletionSource<bool>();
        var cmd = new AsyncRelayCommand(async ct =>
        {
            try
            {
                lambdaStarted.SetResult(true);
                await Task.Delay(10_000, ct);
            }
            catch (OperationCanceledException) { cancelled = true; }
        });

        cmd.Execute(null);
        await lambdaStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cmd.Cancel();
        await Task.Delay(100);

        cancelled.Should().BeTrue();
        cmd.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task CanExecute_FalseWhileRunning()
    {
        var started = new TaskCompletionSource<bool>();
        var finish  = new TaskCompletionSource<bool>();

        var cmd = new AsyncRelayCommand(async ct =>
        {
            started.SetResult(true);
            await finish.Task.WaitAsync(ct);
        });

        cmd.Execute(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cmd.CanExecute(null).Should().BeFalse();

        finish.SetResult(true);
        await Task.Delay(50); // 完了を待つ
        cmd.CanExecute(null).Should().BeTrue();
    }
}
