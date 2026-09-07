using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public class SingleModeSyncCoordinatorTests
{
    private static TimecodeSyncService CreateService() =>
        new(new SyncDecisionEngine(), new TimecodeSyncSeekState());

    // SyncEnabled + CurrentTrack + not seeking + finite + usable duration + |delta|>tolerance → Seek
    private static SyncPlaybackState SeekYieldingState(double playbackSeconds) => new(
        SyncEnabled: true,
        HasCurrentTrack: true,
        IsSeeking: false,
        PlaybackSeconds: playbackSeconds,
        DurationSeconds: 200.0,
        VideoFps: 30.0,
        TimecodeFps: 30.0);

    private static SyncPlaybackState NoSeekState(double playbackSeconds) => new(
        SyncEnabled: false,
        HasCurrentTrack: true,
        IsSeeking: false,
        PlaybackSeconds: playbackSeconds,
        DurationSeconds: 200.0,
        VideoFps: 30.0,
        TimecodeFps: 30.0);

    [Fact]
    public void Apply_NativeSeeking_DoesNotSettleSyntheticTarget_AndResumesLatestRequestAfterCompletion()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new TimecodeSyncService(new SyncDecisionEngine(), new TimecodeSyncSeekState(), clock);
        service.ReportSeekSent(10);
        bool nativeSeeking = true;
        double playback = 10; // mpv can report the requested position before it finishes seeking.
        int positionReads = 0;
        var seekTargets = new List<double>();
        var coordinator = new SingleModeSyncCoordinator(service, new SingleModeSyncEffects(
            GetTimePos: () => { positionReads++; return (0, playback); },
            BuildPlaybackState: SeekYieldingState,
            SeekTo: target => { seekTargets.Add(target); return true; },
            IsNativeSeeking: () => nativeSeeking));

        coordinator.Apply(10).Should().Be(SyncRequestResult.Deferred);
        clock.Advance(TimeSpan.FromSeconds(3));
        coordinator.Apply(10).Should().Be(SyncRequestResult.Deferred);
        coordinator.Apply(30).Should().Be(SyncRequestResult.Deferred);
        positionReads.Should().Be(0);
        service.SeekState.HasPendingSeek.Should().BeTrue();
        service.SeekState.TargetSeconds.Should().Be(10);
        service.SeekState.LastStatus.Should().Be(TimecodeSyncSeekPendingStatus.Pending);
        seekTargets.Should().BeEmpty();

        nativeSeeking = false;
        playback = 11; // A completed seek outside the old target window can now be evaluated.
        coordinator.Apply(30).Should().Be(SyncRequestResult.Complete);
        seekTargets.Should().Equal(30);
        service.SeekState.TargetSeconds.Should().Be(30);
    }

    [Fact]
    public void Apply_NativeSeeking_DoesNotMarkFileLoadedFromSyntheticProgress()
    {
        var service = CreateService();
        service.BeginFileLoad(10, 0);
        bool nativeSeeking = true;
        var coordinator = new SingleModeSyncCoordinator(service, new SingleModeSyncEffects(
            GetTimePos: () => (0, 11),
            BuildPlaybackState: SeekYieldingState,
            SeekTo: _ => true,
            GetTotalRenderedFrames: () => 10,
            IsNativeSeeking: () => nativeSeeking));

        coordinator.Apply(11).Should().Be(SyncRequestResult.Deferred);
        service.IsLoadingFile.Should().BeTrue();

        nativeSeeking = false;
        coordinator.Apply(11).Should().Be(SyncRequestResult.Complete);
        service.IsLoadingFile.Should().BeFalse();
    }

    [Fact]
    public void Apply_DoesNothing_WhenTimePosReadFails()
    {
        var buildCalls = 0;
        var seekCalls = new List<double>();
        var coordinator = new SingleModeSyncCoordinator(
            CreateService(),
            new SingleModeSyncEffects(
                GetTimePos: () => (rc: 1, playbackSeconds: 0.0),
                BuildPlaybackState: _ => { buildCalls++; return SeekYieldingState(0.0); },
                SeekTo: t => { seekCalls.Add(t); return true; }));

        coordinator.Apply(ltcSeconds: 100.0);

        buildCalls.Should().Be(0);
        seekCalls.Should().BeEmpty();
    }

    [Fact]
    public void Apply_DoesNothing_WhenDecisionIsNotSeek()
    {
        var service = CreateService();
        var seekCalls = new List<double>();
        var coordinator = new SingleModeSyncCoordinator(
            service,
            new SingleModeSyncEffects(
                GetTimePos: () => (rc: 0, playbackSeconds: 0.0),
                BuildPlaybackState: NoSeekState,
                SeekTo: t => { seekCalls.Add(t); return true; }));

        coordinator.Apply(ltcSeconds: 100.0);

        seekCalls.Should().BeEmpty();
        service.SeekState.HasPendingSeek.Should().BeFalse();
    }

    [Fact]
    public void Apply_DoesNotSeekButLogs_WhenSuppressed()
    {
        var service = CreateService();
        // ファイルロード中は全シーク抑止（ShouldSuppressSeek == true）
        service.BeginFileLoad(startPositionSeconds: 0.0, renderedFrameCount: 0);
        var seekCalls = new List<double>();
        var coordinator = new SingleModeSyncCoordinator(
            service,
            new SingleModeSyncEffects(
                GetTimePos: () => (rc: 0, playbackSeconds: 0.0),
                BuildPlaybackState: SeekYieldingState,
                SeekTo: t => { seekCalls.Add(t); return true; }));

        coordinator.Apply(ltcSeconds: 100.0);

        seekCalls.Should().BeEmpty();
        service.SeekState.HasPendingSeek.Should().BeFalse();
    }

    [Fact]
    public void Apply_DoesNotSeek_WhenDebounced()
    {
        var service = CreateService();
        // ロード完了直後はデバウンス中（IsDebounced == true）かつ抑止は解除される
        service.BeginFileLoad(startPositionSeconds: 0.0, renderedFrameCount: 0);
        service.TryMarkFileLoaded(playbackSeconds: 1.0, renderedFrameCount: 10).Should().BeTrue();
        service.ShouldSuppressSeek(playbackSeconds: 0.0, toleranceSeconds: 0.2).Should().BeFalse();
        service.IsDebounced().Should().BeTrue();

        var seekCalls = new List<double>();
        var coordinator = new SingleModeSyncCoordinator(
            service,
            new SingleModeSyncEffects(
                GetTimePos: () => (rc: 0, playbackSeconds: 0.0),
                BuildPlaybackState: SeekYieldingState,
                SeekTo: t => { seekCalls.Add(t); return true; }));

        coordinator.Apply(ltcSeconds: 100.0);

        seekCalls.Should().BeEmpty();
    }

    [Fact]
    public void Apply_SeeksAndReportsSeekSent_WhenSeekSucceeds()
    {
        var service = CreateService();
        var seekCalls = new List<double>();
        var coordinator = new SingleModeSyncCoordinator(
            service,
            new SingleModeSyncEffects(
                GetTimePos: () => (rc: 0, playbackSeconds: 0.0),
                BuildPlaybackState: SeekYieldingState,
                SeekTo: t => { seekCalls.Add(t); return true; }));

        coordinator.Apply(ltcSeconds: 100.0);

        seekCalls.Should().ContainSingle().Which.Should().Be(100.0);
        // ReportSeekSent が呼ばれると保留シークが登録される
        service.SeekState.HasPendingSeek.Should().BeTrue();
        service.SeekState.TargetSeconds.Should().Be(100.0);
    }

    [Fact]
    public void Apply_DoesNotReportSeekSent_WhenSeekFails()
    {
        var service = CreateService();
        var seekCalls = new List<double>();
        var coordinator = new SingleModeSyncCoordinator(
            service,
            new SingleModeSyncEffects(
                GetTimePos: () => (rc: 0, playbackSeconds: 0.0),
                BuildPlaybackState: SeekYieldingState,
                SeekTo: t => { seekCalls.Add(t); return false; }));

        coordinator.Apply(ltcSeconds: 100.0);

        seekCalls.Should().ContainSingle().Which.Should().Be(100.0);
        // シーク失敗時は ReportSeekSent されない
        service.SeekState.HasPendingSeek.Should().BeFalse();
    }
}
