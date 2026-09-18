using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class SyncDecisionEngineTests
{
    /// <summary>
    /// D37-a: 粗い判定は 3 サンプル（250ms 窓）そろってから Seek を出す。行き先や許容の検証は
    /// 同じ状態を 3 回評価して、ゲート通過後の決定で行う。
    /// </summary>
    private static SyncDecision DecideAfterGate(SyncDecisionEngine engine, double ltcSeconds, SyncPlaybackState state)
    {
        SyncDecision decision = engine.Decide(ltcSeconds, state);
        for (int i = 0; i < 2; i++)
            decision = engine.Decide(ltcSeconds, state);
        return decision;
    }

    [Fact]
    public void Decide_ReturnsNone_WhenSyncIsDisabled()
    {
        var engine = new SyncDecisionEngine();
        var state = new SyncPlaybackState(
            SyncEnabled: false,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 1.0,
            DurationSeconds: 20.0);

        SyncDecision decision = engine.Decide(10.0, state);

        decision.Action.Should().Be(SyncActionType.None);
    }

    [Fact]
    public void DefaultOptions_UseSixFrameToleranceForLiveSync()
    {
        var engine = new SyncDecisionEngine();
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 10.20,
            DurationSeconds: 20.0,
            VideoFps: 24.0,
            TimecodeFps: 30.0);

        SyncDecision decision = engine.Decide(10.0, state);

        decision.Action.Should().Be(SyncActionType.None);
        decision.ToleranceSeconds.Should().BeApproximately(6.0 / 24.0, 0.0001);
    }

    [Fact]
    public void Decide_ReturnsNone_WhenDifferenceIsInsideTolerance()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 10.04,
            DurationSeconds: 20.0,
            VideoFps: 30.0,
            TimecodeFps: 30.0);

        SyncDecision decision = engine.Decide(10.0, state);

        decision.Action.Should().Be(SyncActionType.None);
    }

    [Fact]
    public void Decide_ReturnsSeek_WhenDifferenceExceedsTolerance()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 4.0,
            DurationSeconds: 20.0,
            VideoFps: 30.0,
            TimecodeFps: 30.0);

        SyncDecision decision = DecideAfterGate(engine, 10.0, state);

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().Be(10.0);
        decision.DeltaSeconds.Should().Be(6.0);
        decision.ToleranceSeconds.Should().BeApproximately(2.0 / 30.0, 0.0001);
    }

    [Fact]
    public void Decide_ClampsTargetToDuration()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 4.0,
            DurationSeconds: 20.0,
            VideoFps: 30.0,
            TimecodeFps: 30.0);

        SyncDecision decision = DecideAfterGate(engine, 25.0, state);

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().Be(20.0);
    }

    [Fact]
    public void Decide_UsesSlowerFrameDurationForTolerance()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 10.07,
            DurationSeconds: 20.0,
            VideoFps: 24.0,
            TimecodeFps: 30.0);

        SyncDecision decision = engine.Decide(10.0, state);

        decision.Action.Should().Be(SyncActionType.None);
        decision.ToleranceSeconds.Should().BeApproximately(2.0 / 24.0, 0.0001);
    }

    [Fact]
    public void Decide_FallsBackToDefaultVideoFps_WhenVideoFpsIsUnavailable()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(
            ToleranceFrames: 2,
            DefaultVideoFps: 24.0,
            DefaultTimecodeFps: 30.0));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 10.07,
            DurationSeconds: 20.0,
            VideoFps: 0.0,
            TimecodeFps: 30.0);

        SyncDecision decision = engine.Decide(10.0, state);

        decision.Action.Should().Be(SyncActionType.None);
        decision.VideoFpsUsed.Should().Be(24.0);
        decision.UsedDefaultVideoFps.Should().BeTrue();
        decision.ToleranceSeconds.Should().BeApproximately(2.0 / 24.0, 0.0001);
    }

    [Fact]
    public void Decide_ReturnsNone_WhenPlayerCannotAcceptSeek()
    {
        var engine = new SyncDecisionEngine();
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: false,
            IsSeeking: false,
            PlaybackSeconds: 4.0,
            DurationSeconds: 20.0);

        SyncDecision decision = engine.Decide(10.0, state);

        decision.Action.Should().Be(SyncActionType.None);
    }

    [Fact]
    public void Decide_ReturnsNone_WhenIsSeeking()
    {
        var engine = new SyncDecisionEngine();
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: true,
            PlaybackSeconds: 4.0,
            DurationSeconds: 20.0);

        SyncDecision decision = engine.Decide(10.0, state);

        decision.Action.Should().Be(SyncActionType.None);
    }

    [Fact]
    public void Decide_ReturnsNone_WhenDurationIsNaN()
    {
        var engine = new SyncDecisionEngine();
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 4.0,
            DurationSeconds: double.NaN);

        SyncDecision decision = engine.Decide(10.0, state);

        decision.Action.Should().Be(SyncActionType.None);
    }

    [Fact]
    public void Decide_ReturnsNone_WhenDurationIsZero()
    {
        var engine = new SyncDecisionEngine();
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 4.0,
            DurationSeconds: 0.0);

        SyncDecision decision = engine.Decide(10.0, state);

        decision.Action.Should().Be(SyncActionType.None);
    }

    [Fact]
    public void Decide_ReturnsNone_WhenLtcSecondsIsNaN()
    {
        var engine = new SyncDecisionEngine();
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 4.0,
            DurationSeconds: 20.0);

        SyncDecision decision = engine.Decide(double.NaN, state);

        decision.Action.Should().Be(SyncActionType.None);
    }

    [Fact]
    public void Decide_ClampsNegativeTargetToZero()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 4.0,
            DurationSeconds: 20.0,
            VideoFps: 30.0,
            TimecodeFps: 30.0);

        SyncDecision decision = DecideAfterGate(engine, -5.0, state);

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().Be(0.0);
        decision.DeltaSeconds.Should().Be(-4.0);
    }

    [Fact]
    public void Decide_SetsUsedDefaultTimecodeFps_WhenTimecodeFpsIsZero()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(
            ToleranceFrames: 2,
            DefaultVideoFps: 30.0,
            DefaultTimecodeFps: 25.0));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 4.0,
            DurationSeconds: 20.0,
            VideoFps: 30.0,
            TimecodeFps: 0.0);

        SyncDecision decision = engine.Decide(10.0, state);

        decision.UsedDefaultTimecodeFps.Should().BeTrue();
        decision.TimecodeFpsUsed.Should().Be(25.0);
    }

    [Theory]
    [InlineData(10.250, SyncActionType.None)]
    [InlineData(9.750, SyncActionType.None)]
    [InlineData(10.251, SyncActionType.Seek)]
    [InlineData(9.749, SyncActionType.Seek)]
    public void Decide_UsesInclusiveToleranceBoundaryWithOneMillisecondOutside(
        double playbackSeconds,
        SyncActionType expectedAction)
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 1));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: playbackSeconds,
            DurationSeconds: 20.0,
            VideoFps: 4.0,
            TimecodeFps: 4.0);

        SyncDecision decision = DecideAfterGate(engine, 10.0, state);

        decision.Action.Should().Be(expectedAction);
        decision.ToleranceSeconds.Should().Be(0.25);
    }

    [Theory]
    [InlineData(0.0, 0.0, 0.0, SyncActionType.None)]
    [InlineData(0.04, 0.0, 0.04, SyncActionType.Seek)]
    [InlineData(0.04, 0.04, 0.04, SyncActionType.None)]
    [InlineData(0.04, 0.0, 0.041, SyncActionType.Seek)]
    public void Decide_HandlesZeroOneFrameAndDurationEndBoundaries(
        double durationSeconds,
        double playbackSeconds,
        double ltcSeconds,
        SyncActionType expectedAction)
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 0));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: playbackSeconds,
            DurationSeconds: durationSeconds,
            VideoFps: 25.0,
            TimecodeFps: 25.0);

        SyncDecision decision = DecideAfterGate(engine, ltcSeconds, state);

        decision.Action.Should().Be(expectedAction);
        if (expectedAction == SyncActionType.Seek)
            decision.TargetSeconds.Should().Be(durationSeconds);
    }

    [Fact]
    public void Decide_ReversePlaybackWithinAndOutsideToleranceProducesNoneThenSeek()
    {
        // 逆走でずれが -40ms → -200ms へ広がる。ゲートは 1 サンプルでの飛びを弾くため、
        // 実時間（40〜200ms かけて広がる）に合わせて時計を進める。
        double now = 0.0;
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 1), null, () => now);
        SyncPlaybackState Within(double playback) => new(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: playback,
            DurationSeconds: 20.0,
            VideoFps: 25.0,
            TimecodeFps: 25.0);

        engine.Decide(9.96, Within(10.0)).Action.Should().Be(SyncActionType.None);

        SyncDecision outside = SyncDecision.None;
        for (int i = 0; i < 8 && outside.Action != SyncActionType.Seek; i++)
        {
            now += 0.05;
            outside = engine.Decide(9.80, Within(10.0));
        }

        outside.Action.Should().Be(SyncActionType.Seek);
        outside.TargetSeconds.Should().Be(9.80);
        outside.DeltaSeconds.Should().BeApproximately(-0.20, 0.0000001);
    }

    [Fact]
    public void Decide_SeekingStateSuppressesLargeTimecodeJumpCompletely()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 0));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: true,
            PlaybackSeconds: 1.0,
            DurationSeconds: 100.0,
            VideoFps: 25.0,
            TimecodeFps: 25.0);

        SyncDecision decision = engine.Decide(90.0, state);

        decision.Action.Should().Be(SyncActionType.None);
        decision.TargetSeconds.Should().Be(0.0);
    }

    // ---- D29: Single の LTC → 素材位置は MediaIn/MediaOut に収める ----

    [Fact]
    public void Decide_ClampsTargetToMediaOut_WhenSet()
    {
        // 尺 58.5、MediaOut 20 のトラックで範囲外の LTC 40 → 終端は尺ではなく MediaOut。
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 4.0,
            DurationSeconds: 58.5,
            VideoFps: 30.0,
            TimecodeFps: 25.0,
            MediaInSeconds: 2.0,
            MediaOutSeconds: 20.0);

        SyncDecision decision = DecideAfterGate(engine, 40.0, state);

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().Be(20.0, "終端静止は MediaOut 基準");
    }

    [Fact]
    public void Decide_ClampsTargetToMediaIn_WhenBelowTheClipStart()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 6.0,
            DurationSeconds: 58.5,
            VideoFps: 30.0,
            TimecodeFps: 25.0,
            MediaInSeconds: 2.0,
            MediaOutSeconds: 20.0);

        SyncDecision decision = DecideAfterGate(engine, 1.0, state);

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().Be(2.0);
    }

    [Fact]
    public void Decide_UsesDurationAsTheEnd_WhenMediaOutIsUnset()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 4.0,
            DurationSeconds: 58.5,
            VideoFps: 30.0,
            TimecodeFps: 25.0,
            MediaInSeconds: 2.0,
            MediaOutSeconds: null);

        SyncDecision decision = DecideAfterGate(engine, 100.0, state);

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().Be(58.5);
    }

    [Fact]
    public void Decide_ClampsTheCompensatedTargetToMediaOut()
    {
        // 先行補償 0.3 秒が乗っても、行き先は MediaOut を超えない（超えると媒体側で
        // クランプされ、範囲外の位置へ着地してしまう）。
        var compensator = new SeekLatencyCompensator(enabled: true);
        compensator.SelectTrack(Guid.NewGuid());
        const long decideQpc = 1_000;
        compensator.MarkSeekDecision(decideQpc);
        compensator.MarkSeekSent();
        compensator.ObserveFrameReady(decideQpc + (long)(System.Diagnostics.Stopwatch.Frequency * 0.3), 1, 1);
        compensator.CompensationSeconds.Should().BeApproximately(0.3, 1e-9);

        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2), compensator);
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 10.0,
            DurationSeconds: 58.5,
            VideoFps: 30.0,
            TimecodeFps: 25.0,
            MediaInSeconds: 2.0,
            MediaOutSeconds: 20.0);

        SyncDecision decision = DecideAfterGate(engine, 19.9, state);

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().BeApproximately(20.0, 1e-9);
    }

    // ---- D37-a: 瞬間値で粗いシークを出さない ----

    [Fact]
    public void Decide_FirstSampleAfterStartup_UsesTheInstantValueForCatchUp()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 4.0,
            DurationSeconds: 20.0,
            VideoFps: 30.0,
            TimecodeFps: 30.0);

        SyncDecision decision = engine.Decide(10.0, state);

        decision.Action.Should().Be(SyncActionType.Seek,
            "起動後の 1 サンプル目は履歴が無く、追従開始の大きなずれに即応する");
        decision.TargetSeconds.Should().Be(10.0);
        decision.DeltaSeconds.Should().Be(6.0);
    }

    [Fact]
    public void Decide_AfterSeekAndReset_GatesUntilTheWindowFills()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2));
        SyncPlaybackState State(double playback) => new(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: playback,
            DurationSeconds: 20.0,
            VideoFps: 30.0,
            TimecodeFps: 30.0);

        // 起動直後の例外を消費する（このサンプルは許容内）。
        engine.Decide(10.0, State(9.98)).Action.Should().Be(SyncActionType.None);

        engine.ResetSeekGate(); // シーク発行・ロードで系列を切ったのと同じ

        engine.Decide(10.0, State(4.0)).Action.Should().Be(SyncActionType.None, "リセット後 1 サンプル目");
        engine.Decide(10.0, State(4.0)).Action.Should().Be(SyncActionType.None, "2 サンプル目");
        SyncDecision third = engine.Decide(10.0, State(4.0));
        third.Action.Should().Be(SyncActionType.Seek, "3 サンプル（150ms）で中央値が許容を超える");
        third.TargetSeconds.Should().Be(10.0);
        third.DeltaSeconds.Should().Be(6.0);
    }

    [Fact]
    public void Decide_OscillatingResidual_DoesNotSeek()
    {
        // 検証機の実測系列（+11.3 / +39.6 / −1.0 / +118.4 / −11.4 / +253 ms、50ms 間隔）。
        // +118.4 と +253 は 50ms で動けない量なので測定の乱れとして弾かれ、中央値も許容内に留まる。
        double now = 0.0;
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(), null, () => now);
        SyncPlaybackState State(double deltaSeconds) => new(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 10.0 - deltaSeconds,
            DurationSeconds: 100.0,
            VideoFps: 25.0,
            TimecodeFps: 25.0);
        double[] oscillationMs = [11.3, 39.6, -1.0, 118.4, -11.4, 253.0];

        foreach (double deltaMs in oscillationMs)
        {
            now += 0.050;
            engine.Decide(10.0, State(deltaMs / 1000.0)).Action.Should().Be(SyncActionType.None,
                $"{deltaMs}ms の 1 サンプルで出さない");
        }
    }

    [Fact]
    public void Decide_ResetSeekGate_RestartsTheWindow()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2));
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: 4.0,
            DurationSeconds: 20.0,
            VideoFps: 30.0,
            TimecodeFps: 30.0);
        engine.Decide(10.0, state);
        engine.Decide(10.0, state);

        engine.ResetSeekGate();

        engine.Decide(10.0, state).Action.Should().Be(SyncActionType.None, "リセット後は窓が埋まるまで出さない");
        engine.Decide(10.0, state).Action.Should().Be(SyncActionType.None);
        engine.Decide(10.0, state).Action.Should().Be(SyncActionType.Seek);
    }
}
