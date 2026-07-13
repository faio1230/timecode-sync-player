namespace TimecodeSyncPlayer;

internal enum PlaylistTimelineOffsetEditStatus
{
    Applied,
    TrackNotFound,
    ParseFailed
}

internal sealed record PlaylistTimelineOffsetEditResult(
    PlaylistTimelineOffsetEditStatus Status,
    int Index,
    PlaylistTrack? OriginalTrack,
    PlaylistTrack? UpdatedTrack,
    int Fps);

internal static class PlaylistTimelineOffsetEditor
{
    public static PlaylistTimelineOffsetEditResult Apply(
        PlaylistState playlist,
        Guid trackId,
        string input,
        bool autoOffset,
        double fallbackFps)
    {
        int index = -1;
        PlaylistTrack? targetTrack = null;
        for (int i = 0; i < playlist.Tracks.Count; i++)
        {
            if (playlist.Tracks[i].Id == trackId)
            {
                index = i;
                targetTrack = playlist.Tracks[i];
                break;
            }
        }

        if (index < 0 || targetTrack == null)
            return new PlaylistTimelineOffsetEditResult(
                PlaylistTimelineOffsetEditStatus.TrackNotFound,
                -1,
                null,
                null,
                (int)Math.Round(fallbackFps));

        int fps = targetTrack.FrameRate > 0
            ? (int)Math.Round(targetTrack.FrameRate.Value)
            : (int)Math.Round(fallbackFps);

        if (!PlaylistTrackFormatter.TryParseTimecode(input, fps, out TimeSpan newOffset))
            return new PlaylistTimelineOffsetEditResult(
                PlaylistTimelineOffsetEditStatus.ParseFailed,
                index,
                targetTrack,
                null,
                fps);

        var updated = targetTrack with { TimelineOffset = newOffset };
        playlist.Tracks[index] = updated;

        if (autoOffset)
            playlist.RecalculateTimelineFrom(index + 1);

        return new PlaylistTimelineOffsetEditResult(
            PlaylistTimelineOffsetEditStatus.Applied,
            index,
            targetTrack,
            updated,
            fps);
    }
}
