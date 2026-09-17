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

    // ---- D33: 範囲外 LTC の終端ホールド ----

    private static SyncPlaybackState ClipState(
        double playbackSeconds, double mediaIn, double? mediaOut, double duration = 200.0) => new(
        SyncEnabled: true,
        HasCurrentTrack: true,
        IsSeeking: false,
        PlaybackSeconds: playbackSeconds,
        DurationSeconds: duration,
        VideoFps: 25.0,
        TimecodeFps: 25.0,
        MediaInSeconds: mediaIn,
        MediaOutSeconds: mediaOut);

    [Fact]
    public void Apply_LtcAboveClipOut_AtBoundary_HoldsWithoutSeek()
    {
        double playback = 25.0;
        var seekCalls = new List<double>();
        var holdCalls = new List<bool>();
        var coordinator = new SingleModeSyncCoordinator(
            CreateService(),
            new SingleModeSyncEffects(
                GetTimePos: () => (rc: 0, playbackSeconds: playback),
                BuildPlaybackState: ps => ClipState(ps, mediaIn: 5.0, mediaOut: 25.0),
                SeekTo: t => { seekCalls.Add(t); return true; },
                SetEndHold: held => holdCalls.Add(held)));

        coordinator.Apply(ltcSeconds: 40.0).Should().Be(SyncRequestResult.Complete);
        coordinator.Apply(ltcSeconds: 41.0).Should().Be(SyncRequestResult.Complete);

        seekCalls.Should().BeEmpty("端に達したらシークしない");
        holdCalls.Should().Equal(new[] { true }, "ホールドは 1 回だけラッチする");
    }

    [Fact]
    public void Apply_LtcAboveClipOut_BeforeBoundary_SeeksToClipOutWithoutHold()
    {
        double playback = 10.0;
        var seekCalls = new List<double>();
        var holdCalls = new List<bool>();
        var coordinator = new SingleModeSyncCoordinator(
            CreateService(),
            new SingleModeSyncEffects(
                GetTimePos: () => (rc: 0, playbackSeconds: playback),
                BuildPlaybackState: ps => ClipState(ps, mediaIn: 5.0, mediaOut: 25.0),
                SeekTo: t => { seekCalls.Add(t); return true; },
                SetEndHold: held => holdCalls.Add(held)));

        coordinator.Apply(ltcSeconds: 40.0);

        seekCalls.Should().ContainSingle().Which.Should().Be(25.0, "D29 の clamp で端へ着地する");
        holdCalls.Should().BeEmpty("端に達する前はホールドしない");
    }

    [Fact]
    public void Apply_LtcBelowClipIn_AtBoundary_HoldsAtClipIn()
    {
        double playback = 5.0;
        var seekCalls = new List<double>();
        var holdCalls = new List<bool>();
        var coordinator = new SingleModeSyncCoordinator(
            CreateService(),
            new SingleModeSyncEffects(
                GetTimePos: () => (rc: 0, playbackSeconds: playback),
                BuildPlaybackState: ps => ClipState(ps, mediaIn: 5.0, mediaOut: 25.0),
                SeekTo: t => { seekCalls.Add(t); return true; },
                SetEndHold: held => holdCalls.Add(held)));

        coordinator.Apply(ltcSeconds: 0.0);

        seekCalls.Should().BeEmpty();
        holdCalls.Should().Equal(true);
    }

    [Fact]
    public void Apply_BoundaryHoldReleased_WhenLtcReturnsInside_ThenSeeksAndFollows()
    {
        double playback = 25.0;
        var seekCalls = new List<double>();
        var holdCalls = new List<bool>();
        var coordinator = new SingleModeSyncCoordinator(
            CreateService(),
            new SingleModeSyncEffects(
                GetTimePos: () => (rc: 0, playbackSeconds: playback),
                BuildPlaybackState: ps => ClipState(ps, mediaIn: 5.0, mediaOut: 25.0),
                SeekTo: t => { seekCalls.Add(t); return true; },
                SetEndHold: held => holdCalls.Add(held)));

        coordinator.Apply(ltcSeconds: 40.0);
        holdCalls.Should().Equal(true);

        // LTC が範囲内（許容分だけ内側）へ戻ったら解除して追従を再開する。
        coordinator.Apply(ltcSeconds: 10.0);

        holdCalls.Should().Equal(true, false);
        seekCalls.Should().ContainSingle().Which.Should().Be(10.0);
    }

    [Fact]
    public void Apply_GeneratedMedia_ClipOutEqualsDuration_HoldsBeforeEos()
    {
        // 生成素材（尺 = MediaOut）: 最終フレームでホールドし、EOS と二重にならない
        // （ホールドは 1 回だけ。EOS まで進んでから再度ホールドしない）。
        double playback = 24.96;
        var holdCalls = new List<bool>();
        var coordinator = new SingleModeSyncCoordinator(
            CreateService(),
            new SingleModeSyncEffects(
                GetTimePos: () => (rc: 0, playbackSeconds: playback),
                BuildPlaybackState: ps => ClipState(ps, mediaIn: 0.0, mediaOut: null, duration: 25.0),
                SeekTo: _ => true,
                SetEndHold: held => holdCalls.Add(held)));

        coordinator.Apply(ltcSeconds: 40.0);
        coordinator.Apply(ltcSeconds: 40.0);

        holdCalls.Should().Equal(true);
    }

    [Fact]
    public void Apply_LtcInsideClipRange_DoesNotHold()
    {
        var holdCalls = new List<bool>();
        var coordinator = new SingleModeSyncCoordinator(
            CreateService(),
            new SingleModeSyncEffects(
                GetTimePos: () => (rc: 0, playbackSeconds: 10.0),
                BuildPlaybackState: ps => ClipState(ps, mediaIn: 5.0, mediaOut: 25.0),
                SeekTo: _ => true,
                SetEndHold: held => holdCalls.Add(held)));

        coordinator.Apply(ltcSeconds: 10.0);

        holdCalls.Should().BeEmpty();
    }

    [Fact]
    public void Apply_BoundaryHoldReleased_WhenPlaybackLeavesTheBoundary()
    {
        // D33: トラック差し替え後のロード直後のように、ラッチ中でも位置が端から離れたら
        // 解除して通常の着地シークに任せる（S-4 の回帰）。
        double playback = 25.0;
        var seekCalls = new List<double>();
        var holdCalls = new List<bool>();
        var coordinator = new SingleModeSyncCoordinator(
            CreateService(),
            new SingleModeSyncEffects(
                GetTimePos: () => (rc: 0, playbackSeconds: playback),
                BuildPlaybackState: ps => ClipState(ps, mediaIn: 5.0, mediaOut: 25.0),
                SeekTo: t => { seekCalls.Add(t); return true; },
                SetEndHold: held => holdCalls.Add(held)));

        coordinator.Apply(ltcSeconds: 40.0);
        holdCalls.Should().Equal(new[] { true });

        playback = 0.0;
        coordinator.Apply(ltcSeconds: 40.0);

        holdCalls.Should().Equal(new[] { true, false });
        seekCalls.Should().ContainSingle().Which.Should().Be(25.0);
    }

    [Fact]
    public void ApplyClipBoundaryHoldOnly_HoldsOnHeldFramesWithoutSeek()
    {
        // D33: LTC が保持（Duplicate）のまま境界へ着地したケース。通常の Apply は走らないため、
        // 保持フレーム用の評価でホールドする（シークはしない）。
        double playback = 25.0;
        var seekCalls = new List<double>();
        var holdCalls = new List<bool>();
        var coordinator = new SingleModeSyncCoordinator(
            CreateService(),
            new SingleModeSyncEffects(
                GetTimePos: () => (rc: 0, playbackSeconds: playback),
                BuildPlaybackState: ps => ClipState(ps, mediaIn: 5.0, mediaOut: 25.0),
                SeekTo: t => { seekCalls.Add(t); return true; },
                SetEndHold: held => holdCalls.Add(held)));

        coordinator.ApplyClipBoundaryHoldOnly(40.0).Should().BeTrue();

        seekCalls.Should().BeEmpty();
        holdCalls.Should().Equal(new[] { true });

        // LTC が範囲内へ戻れば解除する。
        coordinator.ApplyClipBoundaryHoldOnly(10.0).Should().BeFalse();

        seekCalls.Should().BeEmpty();
        holdCalls.Should().Equal(new[] { true, false });
    }
}
