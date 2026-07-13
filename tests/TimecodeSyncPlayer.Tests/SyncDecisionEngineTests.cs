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
}
