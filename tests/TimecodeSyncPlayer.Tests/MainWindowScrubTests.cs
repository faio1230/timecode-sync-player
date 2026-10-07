using System.Reflection;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Gst;
using TimecodeSyncPlayer.Tests.Gst;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.6.6 F-2・F-3: 窓の入口（タイムラインのドラッグ）から、間引き・世代での着地・離したときの 1 本までを通す。
/// ネイティブも音声も開かない（FakePlaybackApi の位置のサンプルで世代を動かす）。
/// </summary>
[Collection("WpfWindow")]
public sealed class MainWindowScrubTests
{
    [Fact]
    public Task TimelineDrag_SendsOneAtATime_LandsByGeneration_AndReleaseClosesIt() => OnUi(() =>
    {
        var playbackApi = new FakePlaybackApi
        {
            // 送った直後の照会の現在世代が 5（シークの世代）。配信はまだシーク前の 4。
            PositionSample = new PlaybackPositionSample(0, PlaybackPositionBasis.Delivered, 4, 0.5, 4, 5),
        };
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        services.AddSingleton<IGstNativeApi>(new FakeGstNative { PlayerCreateResult = new IntPtr(1) });
        services.AddSingleton<IPlaybackApi>(playbackApi);
        using var provider = services.BuildServiceProvider();
        var window = provider.GetRequiredService<MainWindow>();
        try
        {
            provider.GetRequiredService<GstBackendState>().EnsurePlayer().Should().BeTrue();
            var seekBar = (SeekBarInteractionController)Field(window, "_seekBarInteraction");

            Invoke(window, "TimelinePanel_TimelineScrubMoved", new TimelineScrubEventArgs(1.0, 0, started: true));
            Invoke(window, "TimelinePanel_TimelineScrubMoved", new TimelineScrubEventArgs(2.0, 0, started: false));
            Invoke(window, "TimelinePanel_TimelineScrubMoved", new TimelineScrubEventArgs(3.0, 0, started: false));

            playbackApi.Seeks.Should().Equal([1.0], "moves while the first seek is in flight are not sent");
            seekBar.IsSeeking.Should().BeTrue("the drag stops sync correction like the seek bar does");

            // シーク前の世代のフレーム: 着地ではない。
            Invoke(window, "ObserveScrubLanding");
            playbackApi.Seeks.Should().Equal([1.0]);

            // 配信の世代がシークの世代に追いついた: 着地。覚えた最新の目標（3.0）を 1 本送る。
            playbackApi.PositionSample = new PlaybackPositionSample(1, PlaybackPositionBasis.Delivered, 5, 1.0, 5, 6);
            Invoke(window, "ObserveScrubLanding");
            playbackApi.Seeks.Should().Equal([1.0, 3.0]);
            Invoke(window, "ObserveScrubLanding");
            playbackApi.Seeks.Should().Equal([1.0, 3.0], "the new seek (generation 6) has not landed yet");

            Invoke(window, "TimelinePanel_TimelineScrubMoved", new TimelineScrubEventArgs(3.5, 0, started: false));

            // 離した（別の行の上: 行の番号はその行）。飛行中でも必ず送る。
            Invoke(window, "TimelinePanel_TimelineSeekRequested", new TimelineSeekEventArgs(4.0, 2, endsScrub: true));
            playbackApi.Seeks.Should().Equal([1.0, 3.0, 4.0]);
            seekBar.IsSeeking.Should().BeFalse();

            // 離した後の着地では古い目標（3.5）を送らない。
            playbackApi.PositionSample = new PlaybackPositionSample(4, PlaybackPositionBasis.Delivered, 6, 4.0, 6, 6);
            Invoke(window, "ObserveScrubLanding");
            playbackApi.Seeks.Should().Equal([1.0, 3.0, 4.0]);
            ((ScrubSeekThrottle)Field(window, "_scrubThrottle")).InFlight.Should().BeFalse();

            // ただのクリックは今のまま 1 本。
            Invoke(window, "TimelinePanel_TimelineSeekRequested", new TimelineSeekEventArgs(5.0, 1));
            playbackApi.Seeks.Should().Equal([1.0, 3.0, 4.0, 5.0]);
        }
        finally { window.Dispose(); window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task EndedWhileInFlight_ReleasesTheFlight() => OnUi(() =>
    {
        var playbackApi = new FakePlaybackApi
        {
            PositionSample = new PlaybackPositionSample(0, PlaybackPositionBasis.Delivered, 4, 0.5, 4, 5),
        };
        var services = new ServiceCollection();
        App.ConfigureServices(services);
        services.AddSingleton<IGstNativeApi>(new FakeGstNative { PlayerCreateResult = new IntPtr(1) });
        services.AddSingleton<IPlaybackApi>(playbackApi);
        using var provider = services.BuildServiceProvider();
        var window = provider.GetRequiredService<MainWindow>();
        try
        {
            provider.GetRequiredService<GstBackendState>().EnsurePlayer().Should().BeTrue();
            Invoke(window, "TimelinePanel_TimelineScrubMoved", new TimelineScrubEventArgs(9.0, 0, started: true));
            Invoke(window, "TimelinePanel_TimelineScrubMoved", new TimelineScrubEventArgs(9.5, 0, started: false));
            playbackApi.Seeks.Should().Equal([9.0]);

            // EOF: 新しいフレームは来ない。Ended で解き、覚えた目標を送る。
            Invoke(window, "CompleteScrubFlightOnEnded");
            playbackApi.Seeks.Should().Equal([9.0, 9.5]);
        }
        finally { window.Dispose(); window.Close(); }
        return Task.CompletedTask;
    });

    private static object Field(MainWindow window, string name) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static void Invoke(MainWindow window, string method, EventArgs args) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [null, args]);

    private static void Invoke(MainWindow window, string method) =>
        typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, []);

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
