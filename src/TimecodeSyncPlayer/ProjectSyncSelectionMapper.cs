namespace TimecodeSyncPlayer;

public static class ProjectSyncSelectionMapper
{
    public static int GetSyncModeIndex(SyncMode syncMode)
    {
        return syncMode == SyncMode.Single ? 0 : 1;
    }

    public static int GetGapBehaviorIndex(GapBehavior gapBehavior)
    {
        return gapBehavior == GapBehavior.Black ? 0 : 1;
    }
}
