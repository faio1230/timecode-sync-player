namespace TimecodeSyncPlayer.Tests;

public class GapCaptureCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimerRetry_CanCapturePausedSeekWithoutAnotherFrameCallback(
        bool seeking)
    {
        Assert.Equal(seeking ? GapFrameCaptureDecision.None : GapFrameCaptureDecision.RenderAndCapture, GapFrameCaptureCoordinator.Decide(
            GapState.EnteringFreeze, false, true, true, 9.983, 9.983, 60,
            isNativeSeeking: seeking, allowRedraw: true));
    }

    [Fact]
    public void FailedConfirm_DoesNotCompleteOrCacheTarget()
    {
        var handler = new GapFreezeHandler();
        handler.EnterFreezeCapture(Guid.NewGuid(), 9.983, "clip.mp4");
        Assert.False(GapFreezeCaptureOperation.Run(handler, null,
            () => true, _ => false));
        Assert.Equal(GapState.EnteringFreeze, handler.CurrentState);
        Assert.Null(handler.CachedTrackId);
    }

    [Fact]
    public void GapExitAndReentry_CannotBeCompletedByPreviousCapture()
    {
        var handler = new GapFreezeHandler();
        Guid track = Guid.NewGuid();
        handler.EnterFreezeCapture(track, 9.983, "clip.mp4");
        bool result = GapFreezeCaptureOperation.Run(handler, track, () => true, stillCurrent =>
        {
            Assert.True(stillCurrent());
            handler.Reset();
            handler.EnterFreezeCapture(track, 9.983, "clip.mp4");
            handler.CurrentState = GapState.WaitingForFrameStep;
            Assert.False(stillCurrent());
            return true;
        });
        Assert.False(result);
        Assert.Equal(GapState.WaitingForFrameStep, handler.CurrentState);
        Assert.Null(handler.CachedTrackId);
    }

    [Fact]
    public void OnlySuccessfulCurrentConfirm_ConfirmsTarget()
    {
        var handler = new GapFreezeHandler();
        Guid track = Guid.NewGuid();
        handler.EnterFreezeCapture(track, 9.983, "clip.mp4");
        Assert.True(GapFreezeCaptureOperation.Run(handler, track, () => true,
            guard => guard()));
        Assert.Equal(GapState.FreezeComplete, handler.CurrentState);
        Assert.Equal(track, handler.CachedTrackId);
        Assert.Equal(9.983, handler.CachedTargetSeconds);
    }

    [Fact]
    public void RenderInvalidation_CannotConfirmSuccessfulOldAttempt()
    {
        var handler = new GapFreezeHandler();
        handler.EnterFreezeCapture(Guid.NewGuid(), 9.983, "clip.mp4");
        bool current = true;
        Assert.False(GapFreezeCaptureOperation.Run(handler, null, () => current,
            _ => { current = false; return true; }));
        Assert.Null(handler.CachedTrackId);
    }

    [Fact]
    public void ExactSeekTarget_IsCapturedWithoutAdvancingIntoNextFrame()
    {
        Assert.Equal(GapFrameCaptureDecision.RenderAndCapture,
            GapFrameCaptureCoordinator.Decide(GapState.EnteringFreeze, true, true, true,
                599.0 / 60, 599.0 / 60, 60));
    }

    [Theory]
    [InlineData(false, 9.983)]
    [InlineData(true, double.NaN)]
    [InlineData(true, double.PositiveInfinity)]
    [InlineData(true, 11)]
    public void MissingOrOutOfRangePosition_CannotConfirmCapture(bool readable, double position)
    {
        Assert.Equal(GapFrameCaptureDecision.None,
            GapFrameCaptureCoordinator.Decide(GapState.WaitingForFrameStep, true, true, readable,
                position, 599.0 / 60, 60));
    }

    [Fact]
    public void TimeoutFallback_DoesNotCacheAnUnconfirmedRequestedTarget()
    {
        var handler = new GapFreezeHandler();
        handler.EnterFreezeCapture(Guid.NewGuid(), 9.983, "clip.mp4");
        handler.ForceFreezeComplete();
        Assert.Null(handler.CachedTrackId);
        Assert.Equal(0, handler.CachedTargetSeconds);
    }
}
