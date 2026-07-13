using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ContinueOnTrackPlannerTests
{
    [Fact]
    public void Decide_ReturnsSwitchTrack_WhenLoadedTrackDiffers()
    {
        var track = CreateTrack("A");
        var query = new TimelineQueryResult(TimelineQueryStatus.OnTrack, track, 12.5, null);

        ContinueOnTrackDecision result = ContinueOnTrackPlanner.Decide(query, loadedTrackId: Guid.NewGuid());

        result.Action.Should().Be(ContinueOnTrackAction.SwitchTrack);
        result.Track.Should().Be(track);
        result.MediaPositionSeconds.Should().Be(12.5);
    }

    [Fact]
    public void Decide_ReturnsContinueCurrentTrack_WhenLoadedTrackMatches()
    {
        var track = CreateTrack("A");
        var query = new TimelineQueryResult(TimelineQueryStatus.OnTrack, track, 12.5, null);

        ContinueOnTrackDecision result = ContinueOnTrackPlanner.Decide(query, loadedTrackId: track.Id);

        result.Action.Should().Be(ContinueOnTrackAction.ContinueCurrentTrack);
        result.Track.Should().Be(track);
        result.MediaPositionSeconds.Should().Be(12.5);
    }

    private static PlaylistTrack CreateTrack(string name) =>
        new(
            Guid.NewGuid(),
            $"C:\\Videos\\{name}.mp4",
            name,
            TimeSpan.Zero,
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(30),
            TimeSpan.Zero,
            30,
            true);
}
