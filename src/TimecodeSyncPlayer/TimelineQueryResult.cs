namespace TimecodeSyncPlayer;

public sealed record TimelineQueryResult(
    TimelineQueryStatus Status,
    PlaylistTrack? Track,
    double MediaPositionSeconds,
    PlaylistTrack? PreviousTrack,
    PlaylistTrack? NextTrack = null);

public enum TimelineQueryStatus
{
    OnTrack,
    Gap,
    NoTracks
}
