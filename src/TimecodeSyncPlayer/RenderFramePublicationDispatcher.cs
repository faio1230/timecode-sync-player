namespace TimecodeSyncPlayer;

internal static class RenderFramePublicationDispatcher
{
    public static void Execute(
        GapRenderFrameDecision gapDecision,
        Action publishNormalFrame,
        Action captureWithoutPublishing,
        Action? afterFrameProcessed)
    {
        ArgumentNullException.ThrowIfNull(publishNormalFrame);
        ArgumentNullException.ThrowIfNull(captureWithoutPublishing);

        if (gapDecision == GapRenderFrameDecision.None)
            publishNormalFrame();
        else
            captureWithoutPublishing();

        afterFrameProcessed?.Invoke();
    }
}
