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

    /// <summary>
    /// v0.6.6 R-4: 再生中に 1 フレーム送りを押すと、再生/一時停止ボタンと同じ経路で止め（利用者の一時停止）、
    /// 配信フレームの PTS から 1 フレーム先のフレームの中へ、SeekRelative と同じ経路でシークする。
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public Task FrameStep_WhilePlaying_PausesAsUser_AndSeeksThroughTheManualSeekPath(int steps) => OnUi(() =>
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock) { SignalLossMode = LtcSignalLossMode.RunThrough };
        h.AddTrack("first", 0);
        h.Controller.ReceiveFrame(new(new LtcTimecode(0, 0, 1, 0, false), 25, 1), 10_000);
        h.Controller.ReceiveFrame(new(new LtcTimecode(0, 0, 3, 0, false), 25, 3), 10_040);
        h.Controller.ReceiveFrame(new(new LtcTimecode(0, 0, 3, 1, false), 25, 3.04), 10_080);
        h.Operations.Clear();

        var playbackApi = new FakePlaybackApi
        {
            PositionSample = new PlaybackPositionSample(4.0, PlaybackPositionBasis.Delivered, 3, 100 * 1001 / 30000.0, 3, 3),
        };
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
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindow).GetField("_ltcSyncController", flags)!.SetValue(window, h.Controller);
            typeof(MainWindow).GetField("_fps", flags)!.SetValue(window, 30000.0 / 1001);
            var playback = (PlaybackControlState)typeof(MainWindow).GetField("_playbackControl", flags)!.GetValue(window)!;
            playback.TogglePlayPause().IsPaused.Should().BeFalse();
            playbackApi.SetPausedCalls.Clear();

            ((IPlaybackController)window).StepFrame(steps);

            playback.IsPaused.Should().BeTrue();
            playback.UserPauseOwned.Should().BeTrue("the step pauses through the play/pause button path");
            playbackApi.SetPausedCalls.Should().NotBeEmpty().And.OnlyContain(p => p, "the step never resumes playback");
            playbackApi.Seeks.Should().ContainSingle().Which.Should().BeApproximately((100 + steps + 0.5) * 1001 / 30000.0, 1e-9);
            h.AdvancePlayback(2.5, 2);
            for (int i = 0; i < 20; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(100));
                h.Tick100Milliseconds();
            }
            h.Operations.Should().NotContain(o => o.Name == "seek" || o.Name == "loadfile",
                "the frame step cancels the previously deferred automatic request like the 10 second skip");
        }
        finally { window.Dispose(); window.Close(); }
        return Task.CompletedTask;
    });

    /// <summary>v0.6.6 R-4: 配信フレームがまだ無いときは何もしない（止めもしない）。</summary>
    [Fact]
    public Task FrameStep_WithoutDeliveredFrame_DoesNothing() => OnUi(() =>
    {
        var playbackApi = new FakePlaybackApi
        {
            PositionSample = new PlaybackPositionSample(0, PlaybackPositionBasis.None, 0, 0, 0, 1),
        };
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1) };
        services.AddSingleton<IGstNativeApi>(native);
        services.AddSingleton<IPlaybackApi>(playbackApi);
        using var provider = services.BuildServiceProvider();
        var window = provider.GetRequiredService<MainWindow>();
        try
        {
            provider.GetRequiredService<GstBackendState>().EnsurePlayer().Should().BeTrue();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindow).GetField("_fps", flags)!.SetValue(window, 25.0);
            var playback = (PlaybackControlState)typeof(MainWindow).GetField("_playbackControl", flags)!.GetValue(window)!;
            playback.TogglePlayPause();
            playbackApi.SetPausedCalls.Clear();

            ((IPlaybackController)window).StepFrame(1);

            playbackApi.Seeks.Should().BeEmpty();
            playbackApi.SetPausedCalls.Should().BeEmpty();
            playback.IsPaused.Should().BeFalse();
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
