namespace TimecodeSyncPlayer;

internal static class PlaylistDropTargetPolicy
{
    public static int ResolveTargetIndex(int hitIndex, int trackCount)
    {
        if (trackCount <= 0)
            return -1;

        if (hitIndex < 0)
            return trackCount - 1;

        return Math.Min(hitIndex, trackCount - 1);
    }
}
