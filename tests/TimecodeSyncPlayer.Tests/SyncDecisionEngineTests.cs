using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class SyncDecisionEngineTests
{
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

        SyncDecision decision = engine.Decide(10.0, state);

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

        SyncDecision decision = engine.Decide(25.0, state);

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

        SyncDecision decision = engine.Decide(-5.0, state);

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

        SyncDecision decision = engine.Decide(10.0, state);

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

        SyncDecision decision = engine.Decide(ltcSeconds, state);

        decision.Action.Should().Be(expectedAction);
        if (expectedAction == SyncActionType.Seek)
            decision.TargetSeconds.Should().Be(durationSeconds);
    }

    [Fact]
    public void Decide_ReversePlaybackWithinAndOutsideToleranceProducesNoneThenSeek()
    {
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 1));
        SyncPlaybackState Within(double playback) => new(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: false,
            PlaybackSeconds: playback,
            DurationSeconds: 20.0,
            VideoFps: 25.0,
            TimecodeFps: 25.0);

        engine.Decide(9.96, Within(10.0)).Action.Should().Be(SyncActionType.None);
        SyncDecision outside = engine.Decide(9.80, Within(10.0));

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
}
