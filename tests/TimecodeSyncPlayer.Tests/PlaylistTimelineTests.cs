using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistTimelineTests
{
    [Fact]
    public void RecalculateTimelineFrom_PlacesTracksSequentially()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4", "C:\\Videos\\clip2.mp4", "C:\\Videos\\clip3.mp4"]);

        var t1 = state.Tracks[0];
        var t2 = state.Tracks[1];
        var t3 = state.Tracks[2];

        state.UpdateMediaDuration(t1.Id, TimeSpan.FromSeconds(60));
        state.UpdateMediaDuration(t2.Id, TimeSpan.FromSeconds(90));
        state.UpdateMediaDuration(t3.Id, TimeSpan.FromSeconds(45));

        state.Tracks[0].GetActualTimelineOut().TotalSeconds.Should().Be(60);
        state.Tracks[1].GetActualTimelineIn().TotalSeconds.Should().Be(60);
        state.Tracks[1].GetActualTimelineOut().TotalSeconds.Should().Be(150);
        state.Tracks[2].GetActualTimelineIn().TotalSeconds.Should().Be(150);
        state.Tracks[2].GetActualTimelineOut().TotalSeconds.Should().Be(195);
    }

    [Fact]
    public void RecalculateTimelineFrom_StartsFromSpecifiedIndex()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4", "C:\\Videos\\clip2.mp4", "C:\\Videos\\clip3.mp4"]);

        var t1 = state.Tracks[0];
        var t2 = state.Tracks[1];
        var t3 = state.Tracks[2];

        state.UpdateMediaDuration(t1.Id, TimeSpan.FromSeconds(60));
        state.UpdateMediaDuration(t2.Id, TimeSpan.FromSeconds(90));
        state.UpdateMediaDuration(t3.Id, TimeSpan.FromSeconds(45));

        state.RecalculateTimelineFrom(1);

        state.Tracks[0].GetActualTimelineIn().TotalSeconds.Should().Be(0);
        state.Tracks[0].GetActualTimelineOut().TotalSeconds.Should().Be(60);
        state.Tracks[1].GetActualTimelineIn().TotalSeconds.Should().Be(60);
        state.Tracks[1].GetActualTimelineOut().TotalSeconds.Should().Be(150);
        state.Tracks[2].GetActualTimelineIn().TotalSeconds.Should().Be(150);
        state.Tracks[2].GetActualTimelineOut().TotalSeconds.Should().Be(195);
    }

    [Fact]
    public void RecalculateTimelineFrom_UsesPreviousActualTimelineOut()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4", "C:\\Videos\\clip2.mp4"]);

        var t1 = state.Tracks[0];
        var t2 = state.Tracks[1];

        state.UpdateMediaDuration(t1.Id, TimeSpan.FromSeconds(60));
        state.UpdateMediaDuration(t2.Id, TimeSpan.FromSeconds(30));

        var currentT1 = state.Tracks[0];
        var updatedT1 = currentT1 with { TimelineOffset = TimeSpan.FromSeconds(40) };
        state.Tracks[0] = updatedT1;

        state.RecalculateTimelineFrom(1);

        state.Tracks[1].GetActualTimelineIn().TotalSeconds.Should().Be(100);
        state.Tracks[1].GetActualTimelineOut().TotalSeconds.Should().Be(130);
    }

    [Fact]
    public void UpdateMediaDuration_DoesNothing_WhenTrackNotFound()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4"]);
        var t1 = state.Tracks[0];
        state.UpdateMediaDuration(t1.Id, TimeSpan.FromSeconds(60));

        var originalTimelineOut = state.Tracks[0].GetActualTimelineOut();

        state.UpdateMediaDuration(Guid.NewGuid(), TimeSpan.FromSeconds(100));

        state.Tracks[0].GetActualTimelineOut().Should().Be(originalTimelineOut);
    }

    [Fact]
    public void UpdateMediaDuration_RecalculatesSubsequentTracks()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4", "C:\\Videos\\clip2.mp4", "C:\\Videos\\clip3.mp4"]);

        var t1 = state.Tracks[0];
        var t2 = state.Tracks[1];
        var t3 = state.Tracks[2];

        state.UpdateMediaDuration(t1.Id, TimeSpan.FromSeconds(60));
        state.UpdateMediaDuration(t2.Id, TimeSpan.FromSeconds(60));
        state.UpdateMediaDuration(t3.Id, TimeSpan.FromSeconds(60));

        state.Tracks[2].GetActualTimelineIn().TotalSeconds.Should().Be(120);

        state.UpdateMediaDuration(t1.Id, TimeSpan.FromSeconds(100));

        state.Tracks[1].GetActualTimelineIn().TotalSeconds.Should().Be(100);
        state.Tracks[1].GetActualTimelineOut().TotalSeconds.Should().Be(160);
        state.Tracks[2].GetActualTimelineIn().TotalSeconds.Should().Be(160);
        state.Tracks[2].GetActualTimelineOut().TotalSeconds.Should().Be(220);
    }

    [Fact]
    public void GetEffectiveDuration_UsesMediaOut_WhenSet()
    {
        var track = new PlaylistTrack(
            Id: Guid.NewGuid(),
            FilePath: "test.mp4",
            Name: "test",
            MediaIn: TimeSpan.FromSeconds(10),
            MediaOut: TimeSpan.FromSeconds(50),
            TimelineOffset: TimeSpan.Zero,
            MediaDuration: TimeSpan.FromSeconds(100),
            SyncOffset: TimeSpan.Zero,
            FrameRate: null,
            IsEnabled: true);

        track.GetEffectiveDuration().TotalSeconds.Should().Be(40);
    }

    [Fact]
    public void GetEffectiveDuration_UsesMediaDuration_WhenMediaOutIsNull()
    {
        var track = new PlaylistTrack(
            Id: Guid.NewGuid(),
            FilePath: "test.mp4",
            Name: "test",
            MediaIn: TimeSpan.FromSeconds(10),
            MediaOut: null,
            TimelineOffset: TimeSpan.Zero,
            MediaDuration: TimeSpan.FromSeconds(100),
            SyncOffset: TimeSpan.Zero,
            FrameRate: null,
            IsEnabled: true);

        track.GetEffectiveDuration().TotalSeconds.Should().Be(90);
    }

    [Fact]
    public void GetEffectiveDuration_ReturnsZero_WhenNegative()
    {
        var track = new PlaylistTrack(
            Id: Guid.NewGuid(),
            FilePath: "test.mp4",
            Name: "test",
            MediaIn: TimeSpan.FromSeconds(100),
            MediaOut: TimeSpan.FromSeconds(50),
            TimelineOffset: TimeSpan.Zero,
            MediaDuration: TimeSpan.Zero,
            SyncOffset: TimeSpan.Zero,
            FrameRate: null,
            IsEnabled: true);

        track.GetEffectiveDuration().Should().Be(TimeSpan.Zero);
    }
}
