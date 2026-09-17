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
        bool allowRedraw = false,
        bool frameSeenSinceCapture = true)
    {
        if ((!hasFrame && !allowRedraw) || !isExpectedPath || isNativeSeeking)
            return GapFrameCaptureDecision.None;

        double effectiveFps = fps > 0 ? fps : 30.0;
        double frameSeconds = 1.0 / effectiveFps;

        if (state is GapState.EnteringFreeze or GapState.WaitingForFrameStep)
        {
            // D21: 進入・再ロードの後に届いたフレームだけを最終フレームとして固定する。
            if (!frameSeenSinceCapture)
                return GapFrameCaptureDecision.None;

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
