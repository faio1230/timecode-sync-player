namespace TimecodeSyncPlayer;

internal static class PlaylistDragInitiationPolicy
{
    public static bool ShouldBeginDrag(
        bool hasDragStartPoint,
        bool isLeftButtonPressed,
        bool hasSelectedTrack,
        double deltaX,
        double deltaY,
        double minHorizontalDistance,
        double minVerticalDistance)
    {
        if (!hasDragStartPoint || !isLeftButtonPressed || !hasSelectedTrack)
            return false;

        return deltaX >= minHorizontalDistance || deltaY >= minVerticalDistance;
    }
}
