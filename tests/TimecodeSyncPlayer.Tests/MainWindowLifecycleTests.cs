using System.Reflection;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace TimecodeSyncPlayer.Tests;

public sealed class MainWindowLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task LtcCallbacks_QueuedBeforeOrReceivedAfterDisposeDoNotChangeUi(bool receivedAfterDispose) => OnUi(async () =>
    {
        // Construct the real Window without showing it, so no native/audio startup occurs.
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();
        var window = provider.GetRequiredService<MainWindow>();
        try
        {
            window.ViewModel.Sync.IsLtcRunning = true;
            window.ViewModel.Sync.LtcTimecodeText = "before shutdown";
            string format = window.ViewModel.Sync.LtcFormatText;
            if (receivedAfterDispose) window.Dispose();
            // Invoke event boundaries without starting a hardware monitor or the Loaded pipeline.
            typeof(MainWindow).GetMethod("LtcMonitor_FrameReceived", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [null, new LtcFrameReceivedEventArgs(new LtcTimecode(0, 0, 1, 0, false), 25, 1)]);
            typeof(MainWindow).GetMethod("LtcMonitor_Stopped", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [null, new InvalidOperationException("late stop")]);
            window.Dispose();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

            window.ViewModel.Sync.IsLtcRunning.Should().BeTrue();
            window.ViewModel.Sync.LtcTimecodeText.Should().Be("before shutdown");
            window.ViewModel.Sync.LtcFormatText.Should().Be(format);
        }
        finally { window.Close(); }
    });

    private static Task OnUi(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
