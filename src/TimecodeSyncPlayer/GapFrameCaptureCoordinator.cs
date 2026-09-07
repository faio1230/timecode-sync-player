namespace TimecodeSyncPlayer;

internal enum GapFrameCaptureDecision
{
    None,
    RenderAndCapture
}

internal static class GapFrameCaptureCoordinator
{
    public static GapFrameCaptureDecision Decide(
        GapState state,
        bool hasFrame,
        bool isExpectedPath,
        bool hasTimePosition,
        double actualPositionSeconds,
        double targetSeconds,
        double fps,
        bool isNativeSeeking = false,
        bool allowRedraw = false)
    {
        if ((!hasFrame && !allowRedraw) || !isExpectedPath || isNativeSeeking)
            return GapFrameCaptureDecision.None;

        double effectiveFps = fps > 0 ? fps : 30.0;
        double frameSeconds = 1.0 / effectiveFps;

        if (state is GapState.EnteringFreeze or GapState.WaitingForFrameStep)
        {
            return ContinueModePlaybackPolicy.ShouldCaptureFreezeFrameAfterFrameStep(
                hasTimePosition,
                actualPositionSeconds,
                targetSeconds,
                frameSeconds)
                ? GapFrameCaptureDecision.RenderAndCapture
                : GapFrameCaptureDecision.None;
        }

        return GapFrameCaptureDecision.None;
    }
}
