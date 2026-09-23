namespace TimecodeSyncPlayer;

/// <summary>
/// ギャップのフリーズを確定する状態遷移（EnteringFreeze → WaitingForFrameStep → FreezeComplete）。
/// 画像そのものは GPU 合成層が進入時に保存している（ComposeLayer.SaveFreeze）ので、ここでは待たない。
/// 古い世代・古い捕捉要求（ギャップを出て入り直した後など）では確定しない。
/// </summary>
internal static class GapFreezeCaptureOperation
{
    /// <param name="confirm">確定してよいかを返す。渡される関数は「この捕捉要求がまだ有効か」。</param>
    public static bool Run(
        GapFreezeHandler handler,
        Guid? loadedTrackId,
        Func<bool> isRenderCurrent,
        Func<Func<bool>, bool> confirm)
    {
        if (handler.CurrentState != GapState.EnteringFreeze || !isRenderCurrent())
            return false;

        long attempt = handler.CaptureAttemptId;
        handler.CurrentState = GapState.WaitingForFrameStep;
        bool StillCurrent() => isRenderCurrent() &&
            handler.CurrentState == GapState.WaitingForFrameStep &&
            handler.CaptureAttemptId == attempt;

        try
        {
            bool confirmed = confirm(StillCurrent);
            if (!StillCurrent())
                return false;
            if (!confirmed)
            {
                handler.CurrentState = GapState.EnteringFreeze;
                return false;
            }

            handler.OnFreezeComplete(loadedTrackId);
            return true;
        }
        catch
        {
            if (StillCurrent())
                handler.CurrentState = GapState.EnteringFreeze;
            throw;
        }
    }
}
