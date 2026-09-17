using Xunit;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

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
        handler.StartedAt = DateTime.UnixEpoch;
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
        handler.StartedAt = DateTime.UnixEpoch;
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
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 12, 34, 56, TimeSpan.Zero));
        var handler = new GapFreezeHandler(clock);
        var trackId = Guid.NewGuid();

        handler.EnterFreezeCapture(trackId, 42.5, "/path/to/video.mp4");

        handler.CurrentState.Should().Be(GapState.EnteringFreeze);
        handler.PendingTrackId.Should().Be(trackId);
        handler.PendingTargetSeconds.Should().Be(42.5);
        handler.PendingPath.Should().Be("/path/to/video.mp4");
        handler.StartedAt.Should().Be(new DateTime(2026, 9, 7, 12, 34, 56, DateTimeKind.Utc));
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
    public void ForceFreezeComplete_DoesNotCertifyUncapturedTarget()
    {
        var handler = new GapFreezeHandler();
        var trackId = Guid.NewGuid();
        handler.EnterFreezeCapture(trackId, 42.5, "test.mp4");

        handler.ForceFreezeComplete();

        handler.CurrentState.Should().Be(GapState.FreezeComplete);
        handler.StartedAt.Should().Be(DateTime.MinValue);
        handler.CachedTrackId.Should().BeNull();
        handler.CachedTargetSeconds.Should().Be(0);
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

    // ---- D32: タイムアウト後の遅延確定 ----

    [Fact]
    public void ForceFreezeComplete_RetainsLateConfirmTarget_WithoutCertifyingIt()
    {
        var handler = new GapFreezeHandler();
        var trackId = Guid.NewGuid();
        handler.EnterFreezeCapture(trackId, 42.5, "test.mp4");

        handler.ForceFreezeComplete();

        handler.HasLateConfirmTarget.Should().BeTrue();
        handler.LateConfirmTrackId.Should().Be(trackId);
        handler.LateConfirmTargetSeconds.Should().Be(42.5);
        handler.LateConfirmPath.Should().Be("test.mp4");
        // 遅延確定の候補であって、最終画像として認定はしない。
        handler.CachedTrackId.Should().BeNull();
        handler.CachedTargetSeconds.Should().Be(0);
    }

    [Fact]
    public void ReopenCaptureForLateFrame_ReentersWithFrameSeen()
    {
        var handler = new GapFreezeHandler();
        var trackId = Guid.NewGuid();
        handler.EnterFreezeCapture(trackId, 42.5, "test.mp4");
        handler.ForceFreezeComplete();

        handler.ReopenCaptureForLateFrame();

        handler.CurrentState.Should().Be(GapState.EnteringFreeze);
        handler.PendingTrackId.Should().Be(trackId);
        handler.PendingTargetSeconds.Should().Be(42.5);
        handler.PendingPath.Should().Be("test.mp4");
        handler.FrameSeenSinceCapture.Should().BeTrue("届いたフレームで確定する");
        handler.HasLateConfirmTarget.Should().BeFalse();
        handler.CanRetrySeek.Should().BeTrue();
    }

    [Fact]
    public void ReopenCaptureForLateFrame_WithoutLateTarget_DoesNothing()
    {
        var handler = new GapFreezeHandler();
        handler.ForceFreezeComplete();

        handler.ReopenCaptureForLateFrame();

        handler.CurrentState.Should().Be(GapState.FreezeComplete);
        handler.PendingTargetSeconds.Should().Be(0);
    }

    [Fact]
    public void Reset_ClearsLateConfirmTarget()
    {
        var handler = new GapFreezeHandler();
        handler.EnterFreezeCapture(Guid.NewGuid(), 42.5, "test.mp4");
        handler.ForceFreezeComplete();
        handler.HasLateConfirmTarget.Should().BeTrue();

        handler.Reset();

        handler.HasLateConfirmTarget.Should().BeFalse();
        handler.LateConfirmTargetSeconds.Should().Be(0);
        handler.LateConfirmTrackId.Should().BeNull();
        handler.LateConfirmPath.Should().BeNull();
    }

    [Fact]
    public void Reset_ClearsLastReloadAt()
    {
        var handler = new GapFreezeHandler();
        handler.LastReloadAt = DateTime.UnixEpoch;

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

    // ---- D32: 進入目標が変わったときのフリーズ画像の破棄 ----

    [Fact]
    public void ShouldDiscardFrozenFrame_NoKnownTarget_IsFalse()
    {
        var handler = new GapFreezeHandler();

        handler.ShouldDiscardFrozenFrame(Guid.NewGuid(), 42.5, 1.0 / 30).Should().BeFalse();
    }

    [Fact]
    public void ShouldDiscardFrozenFrame_SameTrackAndTarget_IsFalse()
    {
        var handler = new GapFreezeHandler();
        var trackId = Guid.NewGuid();
        handler.EnterFreezeCapture(trackId, 42.5, "test.mp4");
        handler.OnFreezeComplete(trackId);

        // 半フレーム以内の差は同じ目標として扱う（F-1 の周期再進入）。
        handler.ShouldDiscardFrozenFrame(trackId, 42.5, 1.0 / 30).Should().BeFalse();
        handler.ShouldDiscardFrozenFrame(trackId, 42.51, 1.0 / 30).Should().BeFalse();
    }

    [Fact]
    public void ShouldDiscardFrozenFrame_SameTrackDifferentTarget_IsTrue()
    {
        var handler = new GapFreezeHandler();
        var trackId = Guid.NewGuid();
        handler.EnterFreezeCapture(trackId, 42.5, "test.mp4");
        handler.OnFreezeComplete(trackId);

        handler.ShouldDiscardFrozenFrame(trackId, 10.0, 1.0 / 30).Should().BeTrue();
    }

    [Fact]
    public void ShouldDiscardFrozenFrame_DifferentTrackSameTarget_IsTrue()
    {
        var handler = new GapFreezeHandler();
        var trackId = Guid.NewGuid();
        handler.EnterFreezeCapture(trackId, 24.983, "c.mp4");
        handler.OnFreezeComplete(trackId);

        // 同尺の別トラック（同じ最終位置）でも、別の絵なので捨てる。
        handler.ShouldDiscardFrozenFrame(Guid.NewGuid(), 24.983, 1.0 / 60).Should().BeTrue();
    }

    [Fact]
    public void ShouldDiscardFrozenFrame_PendingCaptureTarget_IsCompared()
    {
        var handler = new GapFreezeHandler();
        var trackId = Guid.NewGuid();
        // 捕捉中（Pending）は Cached ではなく Pending と比べる。
        handler.EnterFreezeCapture(trackId, 42.5, "test.mp4");

        handler.ShouldDiscardFrozenFrame(trackId, 42.5, 1.0 / 30).Should().BeFalse();
        handler.ShouldDiscardFrozenFrame(trackId, 40.0, 1.0 / 30).Should().BeTrue();
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

    [Theory]
    [InlineData(29_999_999, false)]
    [InlineData(30_000_000, false)]
    [InlineData(30_000_001, true)]
    public void HasTimedOut_UsesInjectedClockAtThreeSecondBoundary(long elapsedTicks, bool expected)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var handler = new GapFreezeHandler(clock);
        handler.EnterFreezeCapture(Guid.NewGuid(), 42.5, "test.mp4");

        clock.Advance(TimeSpan.FromTicks(elapsedTicks));

        handler.HasTimedOut().Should().Be(expected);
    }

    [Fact]
    public void HasTimedOut_WhenWaitingForFrameStepAndPastTimeout_ReturnsTrue()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var handler = new GapFreezeHandler(clock);
        handler.EnterFreezeCapture(Guid.NewGuid(), 42.5, "test.mp4");
        handler.CurrentState = GapState.WaitingForFrameStep;
        clock.Advance(TimeSpan.FromSeconds(3) + TimeSpan.FromTicks(1));

        handler.HasTimedOut().Should().BeTrue();
    }

    [Fact]
    public void EnterFreezeCaptureWithReload_SetsLastReloadAt()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 12, 34, 56, TimeSpan.Zero));
        var handler = new GapFreezeHandler(clock);
        var trackId = Guid.NewGuid();

        handler.EnterFreezeCaptureWithReload(trackId, 42.5, "/path/to/video.mp4");

        handler.CurrentState.Should().Be(GapState.EnteringFreeze);
        handler.LastReloadAt.Should().Be(new DateTime(2026, 9, 7, 12, 34, 56, DateTimeKind.Utc));
    }

    [Fact]
    public void EnterFreezeCapture_RequiresFrameArrival()
    {
        // D21/D21-b: 進入直後は目標フレームが届いていない。
        var handler = new GapFreezeHandler();

        handler.EnterFreezeCapture(Guid.NewGuid(), 42.5, "test.mp4");

        handler.FrameSeenSinceCapture.Should().BeFalse();
    }

    [Fact]
    public void EnterFreezeCaptureWithCurrentFrame_TrustsDisplayedFrame()
    {
        // D21-b (a): すでに最終フレームを表示している場合は到着を待たない。
        var handler = new GapFreezeHandler();

        handler.EnterFreezeCaptureWithCurrentFrame(Guid.NewGuid(), 42.5, "test.mp4");

        handler.CurrentState.Should().Be(GapState.EnteringFreeze);
        handler.FrameSeenSinceCapture.Should().BeTrue();
    }

    [Fact]
    public void NotifyFrameArrived_OnlyAfterEnterFreezeCapture()
    {
        var handler = new GapFreezeHandler();
        handler.EnterFreezeCapture(Guid.NewGuid(), 42.5, "test.mp4");

        handler.NotifyFrameArrived();

        handler.FrameSeenSinceCapture.Should().BeTrue();
    }

    [Fact]
    public void TryBeginSeekRetry_IsBoundedAndRearmsFrameWait()
    {
        // D21-b (b): 目標位置でないフレームが届いたら再シークし、再びフレーム到着を待つ。
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 12, 34, 56, TimeSpan.Zero));
        var handler = new GapFreezeHandler(clock);
        handler.EnterFreezeCapture(Guid.NewGuid(), 42.5, "test.mp4");
        handler.NotifyFrameArrived();
        clock.Advance(TimeSpan.FromSeconds(1));

        handler.TryBeginSeekRetry().Should().BeTrue();
        handler.SeekRetryCount.Should().Be(1);
        handler.FrameSeenSinceCapture.Should().BeFalse();
        handler.StartedAt.Should().Be(new DateTime(2026, 9, 7, 12, 34, 57, DateTimeKind.Utc));

        handler.TryBeginSeekRetry().Should().BeTrue();
        handler.TryBeginSeekRetry().Should().BeFalse();
        handler.SeekRetryCount.Should().Be(GapFreezeHandler.MaxSeekRetries);
        handler.CanRetrySeek.Should().BeFalse();
    }

    [Fact]
    public void Reset_ClearsSeekRetries()
    {
        var handler = new GapFreezeHandler();
        handler.EnterFreezeCapture(Guid.NewGuid(), 42.5, "test.mp4");
        handler.TryBeginSeekRetry().Should().BeTrue();

        handler.Reset();

        handler.SeekRetryCount.Should().Be(0);
        handler.CanRetrySeek.Should().BeTrue();
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
    public void DecideGapEnter_FreezeBehavior_LoadedPreviousTrackAtFinalFrame_UsesCurrentFrame()
    {
        // D21-b (a): ロード中トラックが直前トラックと同じで、位置が最終フレーム ±1 フレーム。
        var trackId = Guid.NewGuid();
        // 24fps, 60s duration → target = 60 - 1/24 ≈ 59.9583
        double fps = 24.0;
        double duration = 60.0;
        double frameSeconds = 1.0 / fps;
        double expectedTarget = duration - frameSeconds;

        var handler = new GapFreezeHandler();
        // 直前フリーズのキャッシュが残っていても、現在位置の判定を優先する。
        handler.CachedTrackId = trackId;
        handler.CachedTargetSeconds = 10.0;

        var previousTrack = MakeTrack(trackId, duration, fps);
        var result = new TimelineQueryResult(
            Status: TimelineQueryStatus.Gap,
            Track: null,
            MediaPositionSeconds: 0,
            PreviousTrack: previousTrack);

        var action = handler.DecideGapEnter(result, GapBehavior.Freeze, trackId, fps, duration,
            loadedPositionSeconds: expectedTarget);

        action.Type.Should().Be(GapEnterActionType.UseCurrentFrame);
        action.TargetSeconds.Should().BeApproximately(expectedTarget, 0.000001);
        action.DurationSeconds.Should().Be(duration);
        action.Fps.Should().Be(fps);
        action.TrackId.Should().Be(trackId);
        handler.CurrentState.Should().Be(GapState.Inactive);
    }

    [Theory]
    [InlineData(-1.0, true)]
    [InlineData(-2.0, false)]
    public void DecideGapEnter_FreezeBehavior_LoadedPreviousTrackPositionBoundary(
        double offsetFrames, bool expectUseCurrentFrame)
    {
        double fps = 25.0;
        double duration = 50.0;
        double frameSeconds = 1.0 / fps;
        double target = duration - frameSeconds;
        var trackId = Guid.NewGuid();
        var handler = new GapFreezeHandler();
        var previousTrack = MakeTrack(trackId, duration, fps);
        var result = new TimelineQueryResult(TimelineQueryStatus.Gap, null, 0, previousTrack);

        var action = handler.DecideGapEnter(result, GapBehavior.Freeze, trackId, fps, duration,
            loadedPositionSeconds: target + offsetFrames * frameSeconds);

        action.Type.Should().Be(expectUseCurrentFrame
            ? GapEnterActionType.UseCurrentFrame
            : GapEnterActionType.SeekToFinalFrame);
    }

    [Fact]
    public void DecideGapEnter_FreezeBehavior_LoadedPreviousTrackWithUnknownPosition_SeeksToFinalFrame()
    {
        // D21-b (b): 同じトラックでも位置が最終フレームから離れている（または不明）ならシークする。
        var trackId = Guid.NewGuid();
        double fps = 25.0;
        double duration = 50.0;
        double expectedTarget = duration - (1.0 / fps);

        var handler = new GapFreezeHandler();
        handler.CachedTrackId = trackId;
        handler.CachedTargetSeconds = expectedTarget;   // キャッシュがあってもシークする
        var previousTrack = MakeTrack(trackId, duration, fps);
        var result = new TimelineQueryResult(TimelineQueryStatus.Gap, null, 0, previousTrack);

        var action = handler.DecideGapEnter(result, GapBehavior.Freeze, trackId, fps, duration,
            loadedPositionSeconds: null);

        action.Type.Should().Be(GapEnterActionType.SeekToFinalFrame);
        action.TargetSeconds.Should().BeApproximately(expectedTarget, 0.000001);
        action.TrackId.Should().Be(trackId);
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
            currentDurationSeconds: 60.0,
            loadedPositionSeconds: 12.0);

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
            currentDurationSeconds: currentDuration,
            loadedPositionSeconds: 3.0);

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
        double expectedTarget = mediaOut - (1.0 / fps);

        var handler = new GapFreezeHandler();
        var previousTrack = MakeTrack(trackId, durationSeconds: 60.0, fps: fps, mediaOutSeconds: mediaOut);
        var result = new TimelineQueryResult(
            Status: TimelineQueryStatus.Gap,
            Track: null,
            MediaPositionSeconds: 0,
            PreviousTrack: previousTrack);

        var action = handler.DecideGapEnter(result, GapBehavior.Freeze, trackId, fps, 60.0,
            loadedPositionSeconds: 20.0);

        action.Type.Should().Be(GapEnterActionType.SeekToFinalFrame);
        action.TargetSeconds.Should().BeApproximately(expectedTarget, 0.000001);
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

        // FrameRate = null のトラック
        var previousTrack = MakeTrack(trackId, duration, fps: null);
        var result = new TimelineQueryResult(
            Status: TimelineQueryStatus.Gap,
            Track: null,
            MediaPositionSeconds: 0,
            PreviousTrack: previousTrack);

        // currentVideoFps=30 をフォールバックとして使用
        var action = handler.DecideGapEnter(result, GapBehavior.Freeze, trackId, currentFps, duration,
            loadedPositionSeconds: 10.0);

        action.Type.Should().Be(GapEnterActionType.SeekToFinalFrame);
        action.TargetSeconds.Should().BeApproximately(expectedTarget, 0.000001);
        action.Fps.Should().Be(currentFps);
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

    [Fact]
    public void DecideGapEnter_FreezeBehavior_LoadsNextTrackFirstFrame_BeforeFirstTrack()
    {
        // D22: 先頭オフセット領域（前トラックなし）は次のトラックの冒頭フレームを保持する。
        var nextTrackId = Guid.NewGuid();
        var nextTrack = MakeTrack(nextTrackId, durationSeconds: 20.0, fps: 30.0);
        var handler = new GapFreezeHandler();

        var leadingGap = new TimelineQueryResult(
            Status: TimelineQueryStatus.Gap,
            Track: null,
            MediaPositionSeconds: 0,
            PreviousTrack: null,
            NextTrack: nextTrack);

        var action = handler.DecideGapEnter(
            leadingGap, GapBehavior.Freeze, loadedTrackId: null, currentVideoFps: 30.0, currentDurationSeconds: 20.0);

        action.Type.Should().Be(GapEnterActionType.LoadNextTrackFirstFrame);
        action.TrackId.Should().Be(nextTrackId);
        action.TargetSeconds.Should().Be(0.0);
    }

    [Fact]
    public void DecideGapEnter_FreezeBehavior_ReusesCachedNextTrackFirstFrame()
    {
        var nextTrackId = Guid.NewGuid();
        var nextTrack = MakeTrack(nextTrackId, durationSeconds: 20.0, fps: 30.0);
        var handler = new GapFreezeHandler();
        handler.CurrentState = GapState.FreezeComplete;
        handler.CachedTrackId = nextTrackId;
        handler.CachedTargetSeconds = 0.0;

        var leadingGap = new TimelineQueryResult(
            Status: TimelineQueryStatus.Gap,
            Track: null,
            MediaPositionSeconds: 0,
            PreviousTrack: null,
            NextTrack: nextTrack);

        var action = handler.DecideGapEnter(
            leadingGap, GapBehavior.Freeze, loadedTrackId: nextTrackId, currentVideoFps: 30.0, currentDurationSeconds: 20.0);

        action.Type.Should().Be(GapEnterActionType.UseCachedFrame);
        action.TrackId.Should().Be(nextTrackId);
    }
}
