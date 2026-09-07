namespace TimecodeSyncPlayer;

internal static class GapFreezeCaptureOperation
{
    public static async Task<bool> RunAsync(
        GapFreezeHandler handler,
        Guid? loadedTrackId,
        Func<bool> isRenderCurrent,
        Func<Func<bool>, Task<bool>> capture)
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
            bool copied = await capture(StillCurrent);
            if (!StillCurrent())
                return false;
            if (!copied)
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
