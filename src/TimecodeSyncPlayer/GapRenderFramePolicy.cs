namespace TimecodeSyncPlayer;

internal static class GapRenderFramePolicy
{
    public static GapRenderFrameDecision Decide(
        GapState state,
        GapBehavior gapBehavior,
        bool hasFrozenFrame,
        int videoWidth,
        int videoHeight)
    {
        if (state is GapState.BlackFrameActive or GapState.ForceBlack)
            return GapRenderFrameDecision.Black;

        if (state == GapState.FreezeComplete)
            return gapBehavior == GapBehavior.Freeze
                ? GapRenderFrameDecision.GapFreeze
                : GapRenderFrameDecision.Black;

        if (state is GapState.EnteringFreeze or GapState.WaitingForFrameStep)
        {
            if (gapBehavior == GapBehavior.Black)
                return GapRenderFrameDecision.Black;

            return hasFrozenFrame && videoWidth > 0 && videoHeight > 0
                ? GapRenderFrameDecision.GapFreeze
                : GapRenderFrameDecision.Black;
        }

        return GapRenderFrameDecision.None;
    }
}

internal enum GapRenderFrameDecision
{
    None,
    Black,
    GapFreeze
}
