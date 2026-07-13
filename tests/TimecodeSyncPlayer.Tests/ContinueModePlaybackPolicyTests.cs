using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ContinueModePlaybackPolicyTests
{
    [Fact]
    public void ShouldAutoAdvanceAtMediaEnd_ReturnsFalse_ForContinueModeWithTimecodeSync()
    {
        bool result = ContinueModePlaybackPolicy.ShouldAutoAdvanceAtMediaEnd(
            SyncMode.Continue,
            timecodeSyncEnabled: true);

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldAutoAdvanceAtMediaEnd_ReturnsTrue_ForContinueModeWithoutTimecodeSync()
    {
        bool result = ContinueModePlaybackPolicy.ShouldAutoAdvanceAtMediaEnd(
            SyncMode.Continue,
            timecodeSyncEnabled: false);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldAutoAdvanceAtMediaEnd_ReturnsTrue_ForSingleMode()
    {
        bool result = ContinueModePlaybackPolicy.ShouldAutoAdvanceAtMediaEnd(
            SyncMode.Single,
            timecodeSyncEnabled: true);

        result.Should().BeTrue();
    }

    [Fact]
    public void DecideEndAdvanceAction_ReturnsEnterNoTracks_WhenFinalTrackReachedEnd()
    {
        ContinueModeEndAdvanceAction result = ContinueModePlaybackPolicy.DecideEndAdvanceAction(
            positionSeconds: 119.90,
            effectiveDurationSeconds: 120.0,
            hasNextEnabledTrack: false,
            alreadyTriggered: false,
            isPaused: false,
            isSeeking: false,
            thresholdSeconds: 0.15);

        result.Should().Be(ContinueModeEndAdvanceAction.EnterNoTracks);
    }

    [Fact]
    public void ShouldLoadPreviousTrackForGapFreeze_ReturnsTrue_WhenDifferentTrackIsLoaded()
    {
        Guid loadedTrackId = Guid.NewGuid();
        Guid previousTrackId = Guid.NewGuid();

        bool result = ContinueModePlaybackPolicy.ShouldLoadPreviousTrackForGapFreeze(
            loadedTrackId,
            previousTrackId);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldLoadPreviousTrackForGapFreeze_ReturnsFalse_WhenPreviousTrackIsAlreadyLoaded()
    {
        Guid trackId = Guid.NewGuid();

        bool result = ContinueModePlaybackPolicy.ShouldLoadPreviousTrackForGapFreeze(
            trackId,
            trackId);

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldLoadPreviousTrackForGapFreeze_ReturnsFalse_WhenNoPreviousTrackExists()
    {
        bool result = ContinueModePlaybackPolicy.ShouldLoadPreviousTrackForGapFreeze(
            Guid.NewGuid(),
            previousTrackId: null);

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldLoadPreviousTrackForGapFreeze_ReturnsTrue_WhenNoTrackLoadedAndPreviousTrackExists()
    {
        bool result = ContinueModePlaybackPolicy.ShouldLoadPreviousTrackForGapFreeze(
            loadedTrackId: null,
            previousTrackId: Guid.NewGuid());

        result.Should().BeTrue("未再生状態から動画後のGapへ入る場合は、前トラックをロードしてFreeze素材を作る必要がある");
    }

    [Fact]
    public void CanReuseFrozenFrame_ReturnsTrue_ForSameTrackAndNearlySameTarget()
    {
        Guid trackId = Guid.NewGuid();

        bool result = ContinueModePlaybackPolicy.CanReuseFrozenFrame(
            cachedTrackId: trackId,
            cachedTargetSeconds: 237.731,
            requestedTrackId: trackId,
            requestedTargetSeconds: 237.733);

        result.Should().BeTrue();
    }

    [Fact]
    public void CanReuseFrozenFrame_ReturnsFalse_ForDifferentTrack()
    {
        bool result = ContinueModePlaybackPolicy.CanReuseFrozenFrame(
            cachedTrackId: Guid.NewGuid(),
            cachedTargetSeconds: 237.731,
            requestedTrackId: Guid.NewGuid(),
            requestedTargetSeconds: 237.731);

        result.Should().BeFalse();
    }

    [Fact]
    public void CanReuseFrozenFrame_ReturnsFalse_WhenTargetDiffersByMoreThanOneFrame()
    {
        Guid trackId = Guid.NewGuid();

        bool result = ContinueModePlaybackPolicy.CanReuseFrozenFrame(
            cachedTrackId: trackId,
            cachedTargetSeconds: 237.731,
            requestedTrackId: trackId,
            requestedTargetSeconds: 237.900);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsExpectedMediaPath_ReturnsTrue_ForSamePathWithDifferentSeparators()
    {
        bool result = ContinueModePlaybackPolicy.IsExpectedMediaPath(
            @"C:\Videos\clip.mp4",
            "C:/Videos/clip.mp4");

        result.Should().BeTrue();
    }

    [Fact]
    public void IsExpectedMediaPath_ReturnsFalse_ForDifferentPath()
    {
        bool result = ContinueModePlaybackPolicy.IsExpectedMediaPath(
            @"C:\Videos\next.mp4",
            @"C:\Videos\previous.mp4");

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldRenderBlackForGapFreeze_ReturnsTrue_WhenNoPreviousTrackExists()
    {
        bool result = ContinueModePlaybackPolicy.ShouldRenderBlackForGapFreeze(previousTrackId: null);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldRenderBlackForGapFreeze_ReturnsFalse_WhenPreviousTrackExists()
    {
        bool result = ContinueModePlaybackPolicy.ShouldRenderBlackForGapFreeze(Guid.NewGuid());

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldRenderBlackWhileGapActive_ReturnsTrue_ForBlackGapBehavior()
    {
        bool result = ContinueModePlaybackPolicy.ShouldRenderBlackWhileGapActive(
            GapBehavior.Black,
            forceBlackFrame: false);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldRenderBlackWhileGapActive_ReturnsTrue_WhenFreezeGapForcesBlackFrame()
    {
        bool result = ContinueModePlaybackPolicy.ShouldRenderBlackWhileGapActive(
            GapBehavior.Freeze,
            forceBlackFrame: true);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldRenderBlackWhileGapActive_ReturnsFalse_ForNormalFreezeGap()
    {
        bool result = ContinueModePlaybackPolicy.ShouldRenderBlackWhileGapActive(
            GapBehavior.Freeze,
            forceBlackFrame: false);

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldCaptureFreezeFrameAfterFrameStep_ReturnsTrue_WhenTimePositionStaysOnTarget()
    {
        bool result = ContinueModePlaybackPolicy.ShouldCaptureFreezeFrameAfterFrameStep(
            timePosReadSucceeded: true,
            actualPositionSeconds: 237.731,
            targetSeconds: 237.731,
            frameSeconds: 1.0 / 24.0);

        result.Should().BeTrue(
            "mpv は最終フレーム付近の frame-step 後も time-pos が変わらないことがあり、その場合も描画済みフレームをFreezeとして捕捉する必要がある");
    }

    [Fact]
    public void ShouldCopyRenderedFrameToFreezeBuffer_ReturnsTrue_WhileWaitingForFrameStep()
    {
        bool result = ContinueModePlaybackPolicy.ShouldCopyRenderedFrameToFreezeBuffer(GapState.WaitingForFrameStep);

        result.Should().BeTrue(
            "frame-step後に描画されたフレームをFrozenFrameBufferへコピーしないと、Gap Freezeが黒へフォールバックする");
    }

    [Fact]
    public void IsExpectedMediaPath_ReturnsTrue_ForIdenticalPaths()
    {
        bool result = ContinueModePlaybackPolicy.IsExpectedMediaPath(
            @"C:\Videos\clip.mp4",
            @"C:\Videos\clip.mp4");

        result.Should().BeTrue();
    }

    [Fact]
    public void IsExpectedMediaPath_ReturnsTrue_ForTrailingWhitespace()
    {
        bool result = ContinueModePlaybackPolicy.IsExpectedMediaPath(
            @"C:\Videos\clip.mp4  ",
            @"C:\Videos\clip.mp4");

        result.Should().BeTrue();
    }

    [Fact]
    public void IsExpectedMediaPath_ReturnsTrue_ForQuotedPath()
    {
        bool result = ContinueModePlaybackPolicy.IsExpectedMediaPath(
            "\"C:\\Videos\\clip.mp4\"",
            @"C:\Videos\clip.mp4");

        result.Should().BeTrue();
    }

    [Fact]
    public void IsExpectedMediaPath_ReturnsTrue_ForMixedSeparators()
    {
        bool result = ContinueModePlaybackPolicy.IsExpectedMediaPath(
            "D:/Projects/media/file.mov",
            "D:\\Projects\\media\\file.mov");

        result.Should().BeTrue();
    }

    [Fact]
    public void IsExpectedMediaPath_ReturnsFalse_ForDifferentFileSameDirectory()
    {
        bool result = ContinueModePlaybackPolicy.IsExpectedMediaPath(
            @"C:\Videos\clip.mp4",
            @"C:\Videos\other.mp4");

        result.Should().BeFalse();
    }

    [Fact]
    public void IsExpectedMediaPath_ReturnsFalse_ForDifferentExtension()
    {
        bool result = ContinueModePlaybackPolicy.IsExpectedMediaPath(
            @"C:\Videos\clip.mp4",
            @"C:\Videos\clip.mov");

        result.Should().BeFalse();
    }

    [Fact]
    public void CanReuseFrozenFrame_ReturnsFalse_WhenCachedTrackIdIsNull()
    {
        Guid requestedTrackId = Guid.NewGuid();

        bool result = ContinueModePlaybackPolicy.CanReuseFrozenFrame(
            cachedTrackId: null,
            cachedTargetSeconds: 237.731,
            requestedTrackId: requestedTrackId,
            requestedTargetSeconds: 237.731);

        result.Should().BeFalse();
    }

    [Fact]
    public void CanReuseFrozenFrame_ReturnsFalse_WhenRequestedTrackIdIsNull()
    {
        Guid cachedTrackId = Guid.NewGuid();

        bool result = ContinueModePlaybackPolicy.CanReuseFrozenFrame(
            cachedTrackId: cachedTrackId,
            cachedTargetSeconds: 237.731,
            requestedTrackId: null,
            requestedTargetSeconds: 237.731);

        result.Should().BeFalse();
    }

    [Fact]
    public void CanReuseFrozenFrame_ReturnsFalse_WhenCachedTargetIsZero()
    {
        Guid trackId = Guid.NewGuid();

        bool result = ContinueModePlaybackPolicy.CanReuseFrozenFrame(
            cachedTrackId: trackId,
            cachedTargetSeconds: 0,
            requestedTrackId: trackId,
            requestedTargetSeconds: 10.0);

        result.Should().BeFalse();
    }

    [Fact]
    public void CanReuseFrozenFrame_ReturnsTrue_WithCustomTolerance()
    {
        Guid trackId = Guid.NewGuid();

        bool result = ContinueModePlaybackPolicy.CanReuseFrozenFrame(
            cachedTrackId: trackId,
            cachedTargetSeconds: 100.0,
            requestedTrackId: trackId,
            requestedTargetSeconds: 100.3,
            toleranceSeconds: 0.5);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldRenderBlackForGapFreeze_ReturnsFalse_ForEmptyGuid()
    {
        bool result = ContinueModePlaybackPolicy.ShouldRenderBlackForGapFreeze(Guid.Empty);

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldRenderBlackWhileGapActive_WithBlackAndForceBlackBothTrue_ReturnsTrue()
    {
        bool result = ContinueModePlaybackPolicy.ShouldRenderBlackWhileGapActive(
            GapBehavior.Black,
            forceBlackFrame: true);

        result.Should().BeTrue();
    }
}
