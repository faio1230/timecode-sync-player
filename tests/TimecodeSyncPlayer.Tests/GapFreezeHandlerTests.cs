using Xunit;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class GapFreezeHandlerTests
{
    [Fact]
    public void InitialState_IsInactive()
    {
        var handler = new GapFreezeHandler();

        handler.CurrentState.Should().Be(GapState.Inactive);
        handler.IsInactive.Should().BeTrue();
    }

    [Fact]
    public void SetState_ChangesCurrentState()
    {
        var handler = new GapFreezeHandler();

        handler.CurrentState = GapState.EnteringFreeze;

        handler.CurrentState.Should().Be(GapState.EnteringFreeze);
        handler.IsInactive.Should().BeFalse();
    }

    [Fact]
    public void Reset_ReturnsToInactive()
    {
        var handler = new GapFreezeHandler();
        handler.CurrentState = GapState.FreezeComplete;
        handler.StartedAt = DateTime.UtcNow;
        handler.PendingTrackId = Guid.NewGuid();
        handler.PendingTargetSeconds = 123.456;
        handler.PendingPath = "test.mp4";

        handler.Reset();

        handler.CurrentState.Should().Be(GapState.Inactive);
        handler.StartedAt.Should().Be(DateTime.MinValue);
        handler.PendingTrackId.Should().BeNull();
        handler.PendingTargetSeconds.Should().Be(0);
        handler.PendingPath.Should().BeNull();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DecideGapExit_OnlyResumesPauseOwnedByGap(
        bool wasPlaybackPausedBeforeGap,
        bool expectedResume)
    {
        var handler = new GapFreezeHandler { CurrentState = GapState.FreezeComplete };
        handler.RecordPauseOwnership(wasPlaybackPausedBeforeGap);

        GapExitAction action = handler.DecideGapExit();

        action.Type.Should().Be(GapExitActionType.ResumePlayback);
        action.ShouldResumePlayback.Should().Be(expectedResume);
        handler.CurrentState.Should().Be(GapState.Inactive);
    }

    [Fact]
    public void ResetAll_ClearsAllState()
    {
        var handler = new GapFreezeHandler();
        handler.CurrentState = GapState.FreezeComplete;
        handler.StartedAt = DateTime.UtcNow;
        handler.PendingTrackId = Guid.NewGuid();
        handler.PendingTargetSeconds = 123.456;
        handler.PendingPath = "test.mp4";
        handler.CachedTrackId = Guid.NewGuid();
        handler.CachedTargetSeconds = 789.012;

        handler.ResetAll();

        handler.CurrentState.Should().Be(GapState.Inactive);
        handler.StartedAt.Should().Be(DateTime.MinValue);
        handler.PendingTrackId.Should().BeNull();
        handler.PendingTargetSeconds.Should().Be(0);
        handler.PendingPath.Should().BeNull();
        handler.CachedTrackId.Should().BeNull();
        handler.CachedTargetSeconds.Should().Be(0);
    }

    [Fact]
    public void EnterFreezeCapture_SetsStateCorrectly()
    {
        var handler = new GapFreezeHandler();
        var trackId = Guid.NewGuid();
        var before = DateTime.UtcNow;

        handler.EnterFreezeCapture(trackId, 42.5, "/path/to/video.mp4");

        handler.CurrentState.Should().Be(GapState.EnteringFreeze);
        handler.PendingTrackId.Should().Be(trackId);
        handler.PendingTargetSeconds.Should().Be(42.5);
        handler.PendingPath.Should().Be("/path/to/video.mp4");
        handler.StartedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
    }

    [Fact]
    public void OnFreezeComplete_TransitionsStateAndCachesValues()
    {
        var handler = new GapFreezeHandler();
        var trackId = Guid.NewGuid();
        handler.EnterFreezeCapture(trackId, 42.5, "/path/to/video.mp4");

        handler.OnFreezeComplete(Guid.NewGuid());

        handler.CurrentState.Should().Be(GapState.FreezeComplete);
        handler.StartedAt.Should().Be(DateTime.MinValue);
        handler.CachedTrackId.Should().Be(trackId);
        handler.CachedTargetSeconds.Should().Be(42.5);
        handler.PendingTrackId.Should().BeNull();
        handler.PendingTargetSeconds.Should().Be(0);
        handler.PendingPath.Should().BeNull();
    }

    [Fact]
    public void ForceFreezeComplete_SavesPendingValuesToCache()
    {
        var handler = new GapFreezeHandler();
        var trackId = Guid.NewGuid();
        handler.EnterFreezeCapture(trackId, 42.5, "test.mp4");

        handler.ForceFreezeComplete();

        handler.CurrentState.Should().Be(GapState.FreezeComplete);
        handler.StartedAt.Should().Be(DateTime.MinValue);
        handler.CachedTrackId.Should().Be(trackId);          // Pending から引き継ぐ
        handler.CachedTargetSeconds.Should().Be(42.5);       // Pending から引き継ぐ
        handler.PendingTrackId.Should().BeNull();
        handler.PendingTargetSeconds.Should().Be(0);
        handler.PendingPath.Should().BeNull();
    }

    [Fact]
    public void ForceFreezeComplete_WhenNoPendingTrack_SetsNullCache()
    {
        var handler = new GapFreezeHandler();
        // PendingTrackId = null の状態で ForceFreezeComplete

        handler.ForceFreezeComplete();

        handler.CurrentState.Should().Be(GapState.FreezeComplete);
        handler.CachedTrackId.Should().BeNull();
        handler.CachedTargetSeconds.Should().Be(0);
    }

    [Fact]
    public void Reset_ClearsLastReloadAt()
    {
        var handler = new GapFreezeHandler();
        handler.LastReloadAt = DateTime.UtcNow;

        handler.Reset();

        handler.LastReloadAt.Should().Be(DateTime.MinValue);
    }

    [Fact]
    public void TimeoutSec_IsAtLeastThreeSeconds()
    {
        GapFreezeHandler.TimeoutSec.Should().BeGreaterThanOrEqualTo(3.0);
    }

    [Fact]
    public void ShouldStartFreezeCapture_WithFreezeBehaviorAndInactive_ReturnsTrue()
    {
        var handler = new GapFreezeHandler();

        bool result = handler.ShouldStartFreezeCapture(GapBehavior.Freeze);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldStartFreezeCapture_WithBlackBehavior_ReturnsFalse()
    {
        var handler = new GapFreezeHandler();

        bool result = handler.ShouldStartFreezeCapture(GapBehavior.Black);

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldStartFreezeCapture_WhenAlreadyInFreeze_ReturnsFalse()
    {
        var handler = new GapFreezeHandler();
        handler.CurrentState = GapState.EnteringFreeze;

        bool result = handler.ShouldStartFreezeCapture(GapBehavior.Freeze);

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldRenderBlackForGapFreeze_WithBlackBehaviorAndInactive_ReturnsTrue()
    {
        var handler = new GapFreezeHandler();

        bool result = handler.ShouldRenderBlackForGapFreeze(previousTrackId: null);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldTransitionFromFreezeToBlack_WhenInEnteringFreeze_ReturnsTrue()
    {
        var handler = new GapFreezeHandler();
        handler.CurrentState = GapState.EnteringFreeze;

        bool result = handler.ShouldTransitionFromFreezeToBlack(GapBehavior.Black);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldTransitionFromFreezeToBlack_WhenInWaitingForFrameStep_ReturnsTrue()
    {
        var handler = new GapFreezeHandler();
        handler.CurrentState = GapState.WaitingForFrameStep;

        bool result = handler.ShouldTransitionFromFreezeToBlack(GapBehavior.Black);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldTransitionFromFreezeToBlack_WhenInactive_ReturnsFalse()
    {
        var handler = new GapFreezeHandler();

        bool result = handler.ShouldTransitionFromFreezeToBlack(GapBehavior.Black);

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldTransitionFromBlackToFreeze_WhenInBlackFrameActive_ReturnsTrue()
    {
        var handler = new GapFreezeHandler();
        handler.CurrentState = GapState.BlackFrameActive;

        bool result = handler.ShouldTransitionFromBlackToFreeze(GapBehavior.Freeze);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldRenderBlackForGapFreeze_WithNullTrackId_ReturnsTrue()
    {
        var handler = new GapFreezeHandler();

        bool result = handler.ShouldRenderBlackForGapFreeze(previousTrackId: null);

        result.Should().BeTrue();
    }

    [Fact]
    public void CanReuseCachedFrame_WithMatchingTrackAndTarget_ReturnsTrue()
    {
        var handler = new GapFreezeHandler();
        var trackId = Guid.NewGuid();
        handler.CachedTrackId = trackId;
        handler.CachedTargetSeconds = 100.0;

        bool result = handler.CanReuseCachedFrame(trackId, target: 100.01, frameSeconds: 1.0 / 24.0);

        result.Should().BeTrue();
    }

    [Fact]
    public void CanReuseCachedFrame_WithDifferentTrack_ReturnsFalse()
    {
        var handler = new GapFreezeHandler();
        handler.CachedTrackId = Guid.NewGuid();
        handler.CachedTargetSeconds = 100.0;

        bool result = handler.CanReuseCachedFrame(Guid.NewGuid(), target: 100.0, frameSeconds: 1.0 / 24.0);

        result.Should().BeFalse();
    }

    [Fact]
    public void CanReuseCachedFrame_WithZeroTarget_ReturnsFalse()
    {
        var handler = new GapFreezeHandler();
        var trackId = Guid.NewGuid();
        handler.CachedTrackId = trackId;
        handler.CachedTargetSeconds = 100.0;

        bool result = handler.CanReuseCachedFrame(trackId, target: 0, frameSeconds: 1.0 / 24.0);

        result.Should().BeFalse();
    }

    [Fact]
    public void ClearCachedFrame_ResetsValues()
    {
        var handler = new GapFreezeHandler();
        handler.CachedTrackId = Guid.NewGuid();
        handler.CachedTargetSeconds = 100.0;

        handler.ClearCachedFrameInfo();

        handler.CachedTrackId.Should().BeNull();
        handler.CachedTargetSeconds.Should().Be(0);
    }

    [Fact]
    public void HasTimedOut_WhenWithinTimeout_ReturnsFalse()
    {
        var handler = new GapFreezeHandler();
        handler.CurrentState = GapState.EnteringFreeze;
        handler.StartedAt = DateTime.UtcNow;

        bool result = handler.HasTimedOut();

        result.Should().BeFalse();
    }

    [Fact]
    public void HasTimedOut_WhenPastTimeout_ReturnsTrue()
    {
        var handler = new GapFreezeHandler();
        handler.CurrentState = GapState.EnteringFreeze;
        handler.StartedAt = DateTime.UtcNow - TimeSpan.FromSeconds(GapFreezeHandler.TimeoutSec + 0.1);

        bool result = handler.HasTimedOut();

        result.Should().BeTrue();
    }

    [Fact]
    public void EnterFreezeCaptureWithReload_SetsLastReloadAt()
    {
        var handler = new GapFreezeHandler();
        var trackId = Guid.NewGuid();
        var before = DateTime.UtcNow;

        handler.EnterFreezeCaptureWithReload(trackId, 42.5, "/path/to/video.mp4");

        handler.CurrentState.Should().Be(GapState.EnteringFreeze);
        handler.LastReloadAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
    }

    private static PlaylistTrack MakeTrack(Guid id, double durationSeconds, double? fps = 24.0, double? mediaOutSeconds = null)
    {
        return new PlaylistTrack(
            Id: id,
            FilePath: "test.mp4",
            Name: "Test",
            MediaIn: TimeSpan.Zero,
            MediaOut: mediaOutSeconds.HasValue ? TimeSpan.FromSeconds(mediaOutSeconds.Value) : null,
            TimelineOffset: TimeSpan.Zero,
            MediaDuration: TimeSpan.FromSeconds(durationSeconds),
            SyncOffset: TimeSpan.Zero,
            FrameRate: fps,
            IsEnabled: true);
    }

    [Fact]
    public void DecideGapEnter_FreezeBehavior_ReusesCachedFrameWhenTargetMatches()
    {
        var trackId = Guid.NewGuid();
        // 24fps, 60s duration → target = 60 - 1/24 ≈ 59.9583
        double fps = 24.0;
        double duration = 60.0;
        double frameSeconds = 1.0 / fps;
        double expectedTarget = duration - frameSeconds;

        var handler = new GapFreezeHandler();
        handler.CachedTrackId = trackId;
        handler.CachedTargetSeconds = expectedTarget;  // キャッシュに正しいターゲットを設定

        var previousTrack = MakeTrack(trackId, duration, fps);
        var result = new TimelineQueryResult(
            Status: TimelineQueryStatus.Gap,
            Track: null,
            MediaPositionSeconds: 0,
            PreviousTrack: previousTrack);

        // currentVideoFps=24, currentDurationSeconds=60 を渡す
        var action = handler.DecideGapEnter(result, GapBehavior.Freeze, null, fps, duration);

        action.Type.Should().Be(GapEnterActionType.UseCachedFrame);
        action.TargetSeconds.Should().BeApproximately(expectedTarget, 0.000001);
        action.DurationSeconds.Should().Be(duration);
        action.Fps.Should().Be(fps);
        handler.CurrentState.Should().Be(GapState.FreezeComplete);
    }

    [Fact]
    public void DecideGapEnter_FreezeBehavior_LoadPreviousTrackCarriesFinalFrameValues()
    {
        var trackId = Guid.NewGuid();
        double fps = 25.0;
        double mediaOut = 50.0;
        double expectedTarget = mediaOut - (1.0 / fps);

        var handler = new GapFreezeHandler();
        var previousTrack = MakeTrack(trackId, durationSeconds: 60.0, fps: fps, mediaOutSeconds: mediaOut);
        var result = new TimelineQueryResult(
            Status: TimelineQueryStatus.Gap,
            Track: null,
            MediaPositionSeconds: 0,
            PreviousTrack: previousTrack);

        var action = handler.DecideGapEnter(
            result,
            GapBehavior.Freeze,
            loadedTrackId: null,
            currentVideoFps: 30.0,
            currentDurationSeconds: 60.0);

        action.Type.Should().Be(GapEnterActionType.LoadPreviousTrack);
        action.TrackId.Should().Be(trackId);
        action.TargetSeconds.Should().BeApproximately(expectedTarget, 0.000001);
        action.DurationSeconds.Should().Be(mediaOut);
        action.Fps.Should().Be(fps);
    }

    [Fact]
    public void DecideGapEnter_FreezeBehavior_SeekToFinalFrameCarriesFinalFrameValues()
    {
        var trackId = Guid.NewGuid();
        double currentFps = 29.97;
        double currentDuration = 70.0;
        double expectedTarget = currentDuration - (1.0 / currentFps);

        var handler = new GapFreezeHandler();
        var previousTrack = MakeTrack(trackId, durationSeconds: 0, fps: null);
        var result = new TimelineQueryResult(
            Status: TimelineQueryStatus.Gap,
            Track: null,
            MediaPositionSeconds: 0,
            PreviousTrack: previousTrack);

        var action = handler.DecideGapEnter(
            result,
            GapBehavior.Freeze,
            loadedTrackId: trackId,
            currentVideoFps: currentFps,
            currentDurationSeconds: currentDuration);

        action.Type.Should().Be(GapEnterActionType.SeekToFinalFrame);
        action.TrackId.Should().Be(trackId);
        action.TargetSeconds.Should().BeApproximately(expectedTarget, 0.000001);
        action.DurationSeconds.Should().Be(currentDuration);
        action.Fps.Should().Be(currentFps);
    }

    [Fact]
    public void DecideGapEnter_FreezeBehavior_UsesMediaOutWhenAvailable()
    {
        var trackId = Guid.NewGuid();
        // MediaOut=50s, MediaDuration=60s → duration=50, fps=25 → target=50-1/25=49.96
        double fps = 25.0;
        double mediaOut = 50.0;
        double frameSeconds = 1.0 / fps;
        double expectedTarget = mediaOut - frameSeconds;

        var handler = new GapFreezeHandler();
        handler.CachedTrackId = trackId;
        handler.CachedTargetSeconds = expectedTarget;

        var previousTrack = MakeTrack(trackId, durationSeconds: 60.0, fps: fps, mediaOutSeconds: mediaOut);
        var result = new TimelineQueryResult(
            Status: TimelineQueryStatus.Gap,
            Track: null,
            MediaPositionSeconds: 0,
            PreviousTrack: previousTrack);

        var action = handler.DecideGapEnter(result, GapBehavior.Freeze, null, fps, 60.0);

        action.Type.Should().Be(GapEnterActionType.UseCachedFrame);
    }

    [Fact]
    public void DecideGapEnter_FreezeBehavior_FallsBackToCurrentFpsWhenTrackHasNone()
    {
        var trackId = Guid.NewGuid();
        double currentFps = 30.0;
        double duration = 60.0;
        double frameSeconds = 1.0 / currentFps;
        double expectedTarget = duration - frameSeconds;

        var handler = new GapFreezeHandler();
        handler.CachedTrackId = trackId;
        handler.CachedTargetSeconds = expectedTarget;

        // FrameRate = null のトラック
        var previousTrack = MakeTrack(trackId, duration, fps: null);
        var result = new TimelineQueryResult(
            Status: TimelineQueryStatus.Gap,
            Track: null,
            MediaPositionSeconds: 0,
            PreviousTrack: previousTrack);

        // currentVideoFps=30 をフォールバックとして使用
        var action = handler.DecideGapEnter(result, GapBehavior.Freeze, null, currentFps, duration);

        action.Type.Should().Be(GapEnterActionType.UseCachedFrame);
    }

    [Fact]
    public void DecideGapEnter_FreezeBehavior_ReevaluatesToBlackWhenJumpingFromAfterTrackGapToBeforeTrackGap()
    {
        var trackId = Guid.NewGuid();
        var handler = new GapFreezeHandler();
        handler.CurrentState = GapState.FreezeComplete;
        handler.CachedTrackId = trackId;
        handler.CachedTargetSeconds = 59.0;

        var beforeFirstTrackGap = new TimelineQueryResult(
            Status: TimelineQueryStatus.Gap,
            Track: null,
            MediaPositionSeconds: 0,
            PreviousTrack: null);

        var action = handler.DecideGapEnter(beforeFirstTrackGap, GapBehavior.Freeze, trackId, 24.0, 60.0);

        action.Type.Should().Be(GapEnterActionType.ForceBlack);
        handler.CurrentState.Should().Be(GapState.ForceBlack);
        handler.CachedTrackId.Should().BeNull();
        handler.CachedTargetSeconds.Should().Be(0);
    }

    [Fact]
    public void DecideGapEnter_FreezeBehavior_ReevaluatesFromForceBlackWhenPreviousTrackAppears()
    {
        var previousTrackId = Guid.NewGuid();
        var previousTrack = MakeTrack(previousTrackId, durationSeconds: 60.0, fps: 24.0);
        var handler = new GapFreezeHandler();
        handler.CurrentState = GapState.ForceBlack;

        var afterTrackGap = new TimelineQueryResult(
            Status: TimelineQueryStatus.Gap,
            Track: null,
            MediaPositionSeconds: 0,
            PreviousTrack: previousTrack);

        var action = handler.DecideGapEnter(afterTrackGap, GapBehavior.Freeze, loadedTrackId: null, currentVideoFps: 24.0, currentDurationSeconds: 60.0);

        action.Type.Should().Be(GapEnterActionType.LoadPreviousTrack);
        action.TrackId.Should().Be(previousTrackId);
    }

    [Fact]
    public void DecideGapEnter_FreezeBehavior_ReevaluatesWhenFreezeCompleteMovesToDifferentPreviousTrack()
    {
        var cachedTrackId = Guid.NewGuid();
        var requestedTrackId = Guid.NewGuid();
        var requestedTrack = MakeTrack(requestedTrackId, durationSeconds: 60.0, fps: 24.0);
        var handler = new GapFreezeHandler();
        handler.CurrentState = GapState.FreezeComplete;
        handler.CachedTrackId = cachedTrackId;
        handler.CachedTargetSeconds = 59.0;

        var differentAfterTrackGap = new TimelineQueryResult(
            Status: TimelineQueryStatus.Gap,
            Track: null,
            MediaPositionSeconds: 0,
            PreviousTrack: requestedTrack);

        var action = handler.DecideGapEnter(
            differentAfterTrackGap,
            GapBehavior.Freeze,
            loadedTrackId: cachedTrackId,
            currentVideoFps: 24.0,
            currentDurationSeconds: 60.0);

        action.Type.Should().Be(GapEnterActionType.LoadPreviousTrack);
        action.TrackId.Should().Be(requestedTrackId);
    }
}
