using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistEndAdvancePlannerTests
{
    [Fact]
    public void Decide_ReturnsLoadNextTrack_WhenCurrentTrackReachedEndAndNextTrackExists()
    {
        var current = CreateTrack("current", isEnabled: true, durationSeconds: 120);
        var next = CreateTrack("next", isEnabled: true, durationSeconds: 60);

        ContinueModeEndAdvanceDecision result = PlaylistEndAdvancePlanner.Decide(
            new[] { current, next },
            current.Id,
            positionSeconds: 119.90,
            alreadyTriggered: false,
            isPaused: false,
            isSeeking: false,
            thresholdSeconds: 0.15);

        result.Action.Should().Be(ContinueModeEndAdvanceAction.LoadNextTrack);
        result.NextTrack.Should().Be(next);
    }

    [Fact]
    public void Decide_ReturnsEnterNoTracks_WhenCurrentTrackReachedEndAndNoNextEnabledTrackExists()
    {
        var current = CreateTrack("current", isEnabled: true, durationSeconds: 120);
        var disabledNext = CreateTrack("disabled", isEnabled: false, durationSeconds: 60);

        ContinueModeEndAdvanceDecision result = PlaylistEndAdvancePlanner.Decide(
            new[] { current, disabledNext },
            current.Id,
            positionSeconds: 119.90,
            alreadyTriggered: false,
            isPaused: false,
            isSeeking: false,
            thresholdSeconds: 0.15);

        result.Action.Should().Be(ContinueModeEndAdvanceAction.EnterNoTracks);
        result.NextTrack.Should().BeNull();
    }

    [Fact]
    public void Decide_ReturnsNone_WhenLoadedTrackIsNotInPlaylist()
    {
        var track = CreateTrack("track", isEnabled: true, durationSeconds: 120);

        ContinueModeEndAdvanceDecision result = PlaylistEndAdvancePlanner.Decide(
            new[] { track },
            Guid.NewGuid(),
            positionSeconds: 119.90,
            alreadyTriggered: false,
            isPaused: false,
            isSeeking: false,
            thresholdSeconds: 0.15);

        result.Action.Should().Be(ContinueModeEndAdvanceAction.None);
        result.NextTrack.Should().BeNull();
    }

    private static PlaylistTrack CreateTrack(string name, bool isEnabled, double durationSeconds) =>
        new(
            Guid.NewGuid(),
            $@"C:\Videos\{name}.mp4",
            name,
            TimeSpan.Zero,
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(durationSeconds),
            TimeSpan.Zero,
            30,
            isEnabled);
}
