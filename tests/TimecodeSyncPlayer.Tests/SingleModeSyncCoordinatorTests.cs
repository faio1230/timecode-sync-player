using FluentAssertions;

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
    public void Apply_DoesNothing_WhenTimePosReadFails()
    {
        var buildCalls = 0;
        var seekCalls = new List<double>();
        var coordinator = new SingleModeSyncCoordinator(
            CreateService(),
            getTimePos: () => (rc: 1, playbackSeconds: 0.0),
            buildPlaybackState: _ => { buildCalls++; return SeekYieldingState(0.0); },
            seekTo: t => { seekCalls.Add(t); return true; });

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
            getTimePos: () => (rc: 0, playbackSeconds: 0.0),
            buildPlaybackState: NoSeekState,
            seekTo: t => { seekCalls.Add(t); return true; });

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
            getTimePos: () => (rc: 0, playbackSeconds: 0.0),
            buildPlaybackState: SeekYieldingState,
            seekTo: t => { seekCalls.Add(t); return true; });

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
            getTimePos: () => (rc: 0, playbackSeconds: 0.0),
            buildPlaybackState: SeekYieldingState,
            seekTo: t => { seekCalls.Add(t); return true; });

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
            getTimePos: () => (rc: 0, playbackSeconds: 0.0),
            buildPlaybackState: SeekYieldingState,
            seekTo: t => { seekCalls.Add(t); return true; });

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
            getTimePos: () => (rc: 0, playbackSeconds: 0.0),
            buildPlaybackState: SeekYieldingState,
            seekTo: t => { seekCalls.Add(t); return false; });

        coordinator.Apply(ltcSeconds: 100.0);

        seekCalls.Should().ContainSingle().Which.Should().Be(100.0);
        // シーク失敗時は ReportSeekSent されない
        service.SeekState.HasPendingSeek.Should().BeFalse();
    }
}
