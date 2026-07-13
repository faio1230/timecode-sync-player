namespace TimecodeSyncPlayer;

internal sealed record GapEnterActionHandlers(
    Action EnterBlackGap,
    Action ForceBlack,
    Action UseCachedFrame,
    Action<TimelineQueryResult, GapEnterAction> SeekToFinalFrame,
    Action<PlaylistTrack, double, double, double> LoadPreviousTrack);

internal sealed class GapEnterActionDispatcher
{
    private readonly GapEnterActionHandlers _handlers;

    public GapEnterActionDispatcher(GapEnterActionHandlers handlers)
    {
        _handlers = handlers;
    }

    public void Execute(GapEnterAction action, TimelineQueryResult result)
    {
        switch (action.Type)
        {
            case GapEnterActionType.EnterBlackGap:
                _handlers.EnterBlackGap();
                break;
            case GapEnterActionType.ForceBlack:
                _handlers.ForceBlack();
                break;
            case GapEnterActionType.UseCachedFrame:
                _handlers.UseCachedFrame();
                break;
            case GapEnterActionType.SeekToFinalFrame:
                _handlers.SeekToFinalFrame(result, action);
                break;
            case GapEnterActionType.LoadPreviousTrack:
                if (result.PreviousTrack != null &&
                    action.TargetSeconds.HasValue &&
                    action.DurationSeconds.HasValue &&
                    action.Fps.HasValue)
                {
                    _handlers.LoadPreviousTrack(
                        result.PreviousTrack,
                        action.TargetSeconds.Value,
                        action.DurationSeconds.Value,
                        action.Fps.Value);
                }
                break;
        }
    }
}
