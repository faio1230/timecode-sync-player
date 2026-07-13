using FluentAssertions;
using System.IO;

namespace TimecodeSyncPlayer.Tests;

public class ProjectLoadApplicatorTests
{
    [Fact]
    public void Apply_AppliesTracksAndReturnsSyncSelections()
    {
        string mediaPath = Path.GetTempFileName();
        try
        {
            var playlist = new PlaylistState();
            var project = new ProjectData
            {
                SyncMode = SyncMode.Continue,
                GapBehavior = GapBehavior.Freeze,
                Tracks =
                [
                    new TrackData
                    {
                        Id = Guid.NewGuid(),
                        FilePath = mediaPath,
                        Name = "Track",
                        MediaDuration = TimeSpan.FromSeconds(10),
                        IsEnabled = true
                    }
                ]
            };
            var applicator = new ProjectLoadApplicator(playlist);

            ProjectLoadApplyResult result = applicator.Apply(project);

            playlist.Tracks.Should().ContainSingle();
            playlist.Current.Should().NotBeNull();
            result.SyncModeIndex.Should().Be(1);
            result.GapBehaviorIndex.Should().Be(1);
        }
        finally
        {
            File.Delete(mediaPath);
        }
    }
}
