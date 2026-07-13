namespace TimecodeSyncPlayer;

internal static class PlaylistCurrentTrackLabelFormatter
{
    private const string NoTrackLabel = "No track";
    private const string GapBlackLabel = "Gap: Black";
    private const string GapFreezeLabel = "Gap: Freeze";

    public static string Format(
        SyncMode syncMode,
        GapBehavior gapBehavior,
        bool isGapInactive,
        IReadOnlyList<PlaylistTrack> tracks,
        int currentIndex,
        Guid? loadedTrackId)
    {
        if (syncMode == SyncMode.Continue)
        {
            if (!isGapInactive)
                return gapBehavior == GapBehavior.Black ? GapBlackLabel : GapFreezeLabel;

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
}
