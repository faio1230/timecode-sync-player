namespace TimecodeSyncPlayer;

internal enum ContinueOnTrackAction
{
    SwitchTrack,
    ContinueCurrentTrack
}

internal sealed record ContinueOnTrackDecision(
    ContinueOnTrackAction Action,
    PlaylistTrack Track,
    double MediaPositionSeconds);

internal static class ContinueOnTrackPlanner
{
    public static ContinueOnTrackDecision Decide(TimelineQueryResult result, Guid? loadedTrackId)
    {
        PlaylistTrack track = result.Track!;
        ContinueOnTrackAction action = loadedTrackId != track.Id
            ? ContinueOnTrackAction.SwitchTrack
            : ContinueOnTrackAction.ContinueCurrentTrack;

        return new ContinueOnTrackDecision(action, track, result.MediaPositionSeconds);
    }
}
