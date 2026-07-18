namespace TimecodeSyncPlayer;

internal static class PlaylistCurrentTrackLabelFormatter
{
    private const string NoTrackLabel = "No track";
    public static string Format(
        SyncMode syncMode,
        GapBehavior gapBehavior,
        bool isGapInactive,
        IReadOnlyList<PlaylistTrack> tracks,
        int currentIndex,
        Guid? loadedTrackId,
        double timelineSeconds)
    {
        if (syncMode == SyncMode.Continue)
        {
            if (!isGapInactive)
                return FormatGap(gapBehavior, tracks, timelineSeconds);

            PlaylistTrack? loadedTrack = loadedTrackId.HasValue
                ? tracks.FirstOrDefault(track => track.Id == loadedTrackId.Value)
                : null;

            return loadedTrack != null ? $"Sync: {loadedTrack.Name}" : NoTrackLabel;
        }

        if (currentIndex < 0 || currentIndex >= tracks.Count)
            return NoTrackLabel;

        PlaylistTrack current = tracks[currentIndex];
        return $"{currentIndex + 1}/{tracks.Count}  {current.Name}";
    }

    private static string FormatGap(
        GapBehavior gapBehavior,
        IReadOnlyList<PlaylistTrack> tracks,
        double timelineSeconds)
    {
        string gapLabel = gapBehavior == GapBehavior.Black ? "Gap: Black" : "Gap: Freeze";
        PlaylistTrack? nextTrack = tracks
            .Where(track => track.IsEnabled &&
                            track.GetActualTimelineIn().TotalSeconds > timelineSeconds)
            .OrderBy(track => track.GetActualTimelineIn())
            .FirstOrDefault();

        if (nextTrack == null)
            return $"{gapLabel} (以降なし)";

        TimeSpan start = nextTrack.GetActualTimelineIn();
        string startText = $"{(int)start.TotalHours:D2}:{start.Minutes:D2}:{start.Seconds:D2}";
        return $"{gapLabel} → {nextTrack.Name} @ {startText}";
    }
}
