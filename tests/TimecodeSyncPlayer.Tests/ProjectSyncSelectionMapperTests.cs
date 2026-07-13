using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ProjectSyncSelectionMapperTests
{
    [Theory]
    [InlineData(SyncMode.Single, 0)]
    [InlineData(SyncMode.Continue, 1)]
    public void GetSyncModeIndex_MapsSyncModeToViewModelIndex(SyncMode syncMode, int expected)
    {
        ProjectSyncSelectionMapper.GetSyncModeIndex(syncMode).Should().Be(expected);
    }

    [Theory]
    [InlineData(GapBehavior.Black, 0)]
    [InlineData(GapBehavior.Freeze, 1)]
    public void GetGapBehaviorIndex_MapsGapBehaviorToViewModelIndex(GapBehavior gapBehavior, int expected)
    {
        ProjectSyncSelectionMapper.GetGapBehaviorIndex(gapBehavior).Should().Be(expected);
    }
}
