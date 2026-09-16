namespace TimecodeSyncPlayer;

internal static class GapRenderFramePolicy
{
    /// <summary>
    /// Gap 中に合成層へ要求する描画。Freeze 指定の FreezeComplete は必ず GapFreeze を要求し、
    /// 進入時のソース画像の保存は GPU 合成層（ComposeLayer.SaveFreeze）が行う。
    /// </summary>
    public static GapRenderFrameDecision Decide(GapState state, GapBehavior gapBehavior)
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

            // 最終フレームの確定（FreezeComplete）まで、いま見えている画像を保持する。
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
