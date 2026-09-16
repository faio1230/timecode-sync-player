using System.Reflection;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Gst;
using TimecodeSyncPlayer.Tests.Gst;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

[Collection("WpfWindow")]
public sealed class MainWindowManualSeekTests
{
    [Theory]
    [InlineData("back", true)]
    [InlineData("back", false)]
    [InlineData("forward", true)]
    [InlineData("forward", false)]
    [InlineData("timeline", true)]
    [InlineData("timeline", false)]
    public Task ManualSeekEntry_CancelsDeferredSyncBeforeTheNextTick(string entry, bool paused) => OnUi(() =>
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock) { SignalLossMode = LtcSignalLossMode.RunThrough };
        h.AddTrack("first", 0);
        h.Controller.ReceiveFrame(new(new LtcTimecode(0, 0, 1, 0, false), 25, 1), 10_000);
        h.Controller.ReceiveFrame(new(new LtcTimecode(0, 0, 3, 0, false), 25, 3), 10_040);
        h.Controller.ReceiveFrame(new(new LtcTimecode(0, 0, 3, 1, false), 25, 3.04), 10_080);
        h.Operations.Clear();

        // Exercise the real Window entry points without showing it or starting native/audio I/O.
        var playbackApi = new FakePlaybackApi();
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1) };
        services.AddSingleton<IGstNativeApi>(native);
        services.AddSingleton<IPlaybackApi>(playbackApi);
        using var provider = services.BuildServiceProvider();
        var window = provider.GetRequiredService<MainWindow>();
        try
        {
            GstBackendState backend = provider.GetRequiredService<GstBackendState>();
            backend.EnsurePlayer().Should().BeTrue();
            typeof(MainWindow).GetField("_ltcSyncController", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, h.Controller);
            var playback = (PlaybackControlState)typeof(MainWindow).GetField("_playbackControl", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            playback.SetPaused(paused);
            switch (entry)
            {
                case "back": ((IPlaybackController)window).SeekRelative(-10); break;
                case "forward": ((IPlaybackController)window).SeekRelative(10); break;
                case "timeline":
                    typeof(MainWindow).GetMethod("TimelinePanel_TimelineSeekRequested", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(window, [null, new TimelineSeekEventArgs(2.5, 0)]);
                    break;
            }
            double expectedSeek = entry switch
            {
                "back" => -10,
                "forward" => 10,
                _ => 2.5,
            };
            playbackApi.Seeks.Should().ContainSingle().Which.Should().Be(expectedSeek);
            playbackApi.SetPausedCalls.Should().Contain(paused);
            h.AdvancePlayback(2.5, 2);
            for (int i = 0; i < 20; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                h.Tick100Milliseconds();
            }
            h.Operations.Should().NotContain(o => o.Name == "seek" || o.Name == "loadfile",
                "the user's native seek must cancel the previously deferred automatic request");
        }
        finally { window.Dispose(); window.Close(); }
        return Task.CompletedTask;
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
