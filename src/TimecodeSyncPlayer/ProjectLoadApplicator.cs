namespace TimecodeSyncPlayer;

internal sealed record ProjectLoadApplyResult(
    int SyncModeIndex,
    int GapBehaviorIndex,
    IReadOnlyList<SkippedProjectTrack> SkippedTracks);

public sealed class ProjectLoadApplicator
{
    private readonly PlaylistState _playlist;

    public ProjectLoadApplicator(PlaylistState playlist)
    {
        _playlist = playlist;
    }

    internal ProjectLoadApplyResult Apply(ProjectData project)
    {
        IReadOnlyList<SkippedProjectTrack> skipped = ProjectSerializer.ApplyToPlaylist(project, _playlist);
        return new ProjectLoadApplyResult(
            ProjectSyncSelectionMapper.GetSyncModeIndex(project.SyncMode),
            ProjectSyncSelectionMapper.GetGapBehaviorIndex(project.GapBehavior),
            skipped);
    }
}
