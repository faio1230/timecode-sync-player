namespace TimecodeSyncPlayer;

internal static class GapRenderFramePolicy
{
    public static GapRenderFrameDecision Decide(
        GapState state,
        GapBehavior gapBehavior,
        bool hasConfirmedFrame,
        int videoWidth,
        int videoHeight)
    {
        if (state is GapState.BlackFrameActive or GapState.ForceBlack)
            return GapRenderFrameDecision.Black;

        if (state == GapState.FreezeComplete)
            return gapBehavior == GapBehavior.Freeze
                ? hasConfirmedFrame ? GapRenderFrameDecision.GapFreeze : GapRenderFrameDecision.Hold
                : GapRenderFrameDecision.Black;

        if (state is GapState.EnteringFreeze or GapState.WaitingForFrameStep)
        {
            if (gapBehavior == GapBehavior.Black)
                return GapRenderFrameDecision.Black;

            // Keep the image already visible while the explicit final-frame capture
            // completes. Frozen buffers can belong to a completely different clip.
            return GapRenderFrameDecision.Hold;
        }

        return GapRenderFrameDecision.None;
    }
}

internal enum GapRenderFrameDecision
{
    None,
    Black,
    GapFreeze,
    Hold
}
