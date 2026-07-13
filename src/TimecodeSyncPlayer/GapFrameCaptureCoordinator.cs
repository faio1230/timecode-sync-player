namespace TimecodeSyncPlayer;

internal enum GapFrameCaptureDecision
{
    None,
    SendFrameStep,
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
        double fps)
    {
        if (!hasFrame || !isExpectedPath)
            return GapFrameCaptureDecision.None;

        double effectiveFps = fps > 0 ? fps : 30.0;
        double frameSeconds = 1.0 / effectiveFps;

        if (state == GapState.EnteringFreeze)
        {
            double tolerance = frameSeconds * 2.0;
            return hasTimePosition && Math.Abs(actualPositionSeconds - targetSeconds) <= tolerance
                ? GapFrameCaptureDecision.SendFrameStep
                : GapFrameCaptureDecision.None;
        }

        if (state == GapState.WaitingForFrameStep)
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
