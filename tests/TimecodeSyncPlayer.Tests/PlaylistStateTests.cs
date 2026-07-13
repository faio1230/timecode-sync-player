using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistStateTests
{
    [Fact]
    public void AddFiles_SelectsFirstTrack_WhenPlaylistIsEmpty()
    {
        var state = new PlaylistState();

        state.AddFiles(["C:\\Videos\\intro.mov", "C:\\Videos\\main.mp4"]);

        state.Tracks.Should().HaveCount(2);
        state.CurrentIndex.Should().Be(0);
        state.Current.Should().BeSameAs(state.Tracks[0]);
        state.Current!.FilePath.Should().Be("C:\\Videos\\intro.mov");
        state.Current.Name.Should().Be("intro");
        state.Current.MediaIn.Should().Be(TimeSpan.Zero);
        state.Current.MediaOut.Should().BeNull();
        state.Current.TimelineOffset.Should().Be(TimeSpan.Zero);
        state.Current.SyncOffset.Should().Be(TimeSpan.Zero);
        state.Current.FrameRate.Should().BeNull();
        state.Current.IsEnabled.Should().BeTrue();
        state.Tracks.Select(track => track.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void MoveNextAndMovePrevious_UpdateCurrentTrack()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov", "C:\\Videos\\two.mov", "C:\\Videos\\three.mov"]);

        state.MoveNext().Should().BeTrue();
        state.CurrentIndex.Should().Be(1);
        state.Current!.Name.Should().Be("two");

        state.MovePrevious().Should().BeTrue();
        state.CurrentIndex.Should().Be(0);
        state.Current!.Name.Should().Be("one");
    }

    [Fact]
    public void MoveNext_ReturnsFalseAtEnd_AndKeepsCurrentTrack()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov", "C:\\Videos\\two.mov"]);
        state.Select(1).Should().BeTrue();
        PlaylistTrack current = state.Current!;

        state.MoveNext().Should().BeFalse();

        state.CurrentIndex.Should().Be(1);
        state.Current.Should().BeSameAs(current);
    }

    [Fact]
    public void RemoveAt_MovesSelectionToNextTrack_WhenRemovingCurrentTrackBeforeEnd()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov", "C:\\Videos\\two.mov", "C:\\Videos\\three.mov"]);
        state.Select(1).Should().BeTrue();

        state.RemoveAt(1).Should().BeTrue();

        state.Tracks.Should().HaveCount(2);
        state.CurrentIndex.Should().Be(1);
        state.Current!.Name.Should().Be("three");
    }

    [Fact]
    public void RemoveAt_MovesSelectionToPreviousTrack_WhenRemovingCurrentTrackAtEnd()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov", "C:\\Videos\\two.mov"]);
        state.Select(1).Should().BeTrue();

        state.RemoveAt(1).Should().BeTrue();

        state.Tracks.Should().ContainSingle();
        state.CurrentIndex.Should().Be(0);
        state.Current!.Name.Should().Be("one");
    }

    [Fact]
    public void RemoveAt_BeforeCurrent_DecrementsCurrentIndex()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov", "C:\\Videos\\two.mov", "C:\\Videos\\three.mov"]);
        state.Select(2).Should().BeTrue();

        state.RemoveAt(0).Should().BeTrue();

        state.CurrentIndex.Should().Be(1);
        state.Current!.Name.Should().Be("three");
    }

    [Fact]
    public void InvalidOperations_ReturnFalse_AndKeepCurrentTrack()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov"]);
        PlaylistTrack current = state.Current!;

        state.Select(-1).Should().BeFalse();
        state.Select(1).Should().BeFalse();
        state.RemoveAt(-1).Should().BeFalse();
        state.RemoveAt(1).Should().BeFalse();
        state.MovePrevious().Should().BeFalse();
        state.MoveNext().Should().BeFalse();

        state.CurrentIndex.Should().Be(0);
        state.Current.Should().BeSameAs(current);
    }

    [Fact]
    public void MoveTrackUp_ReordersSelectedTrack_AndKeepsCurrentTrackIdentity()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov", "C:\\Videos\\two.mov", "C:\\Videos\\three.mov"]);
        PlaylistTrack current = state.Current!;

        state.MoveTrackUp(2).Should().BeTrue();

        state.Tracks.Select(track => track.Name).Should().Equal("one", "three", "two");
        state.Current.Should().BeSameAs(current);
        state.CurrentIndex.Should().Be(0);
    }

    [Fact]
    public void MoveTrackDown_ReordersSelectedTrack_AndUpdatesCurrentIndex_WhenCurrentMoves()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov", "C:\\Videos\\two.mov", "C:\\Videos\\three.mov"]);
        PlaylistTrack current = state.Current!;

        state.MoveTrackDown(0).Should().BeTrue();

        state.Tracks.Select(track => track.Name).Should().Equal("two", "one", "three");
        state.Current.Should().BeSameAs(current);
        state.CurrentIndex.Should().Be(1);
    }

    [Fact]
    public void MoveTrackUpDown_ReturnFalseAtBoundaries()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov", "C:\\Videos\\two.mov"]);
        PlaylistTrack current = state.Current!;

        state.MoveTrackUp(0).Should().BeFalse();
        state.MoveTrackUp(-1).Should().BeFalse();
        state.MoveTrackDown(1).Should().BeFalse();
        state.MoveTrackDown(2).Should().BeFalse();

        state.Tracks.Select(track => track.Name).Should().Equal("one", "two");
        state.Current.Should().BeSameAs(current);
        state.CurrentIndex.Should().Be(0);
    }

    [Fact]
    public void MoveTrack_ReordersArbitraryIndexes_AndKeepsCurrentTrackIdentity()
    {
        var state = new PlaylistState();
        state.AddFiles([
            "C:\\Videos\\one.mov",
            "C:\\Videos\\two.mov",
            "C:\\Videos\\three.mov",
            "C:\\Videos\\four.mov"
        ]);
        state.Select(1).Should().BeTrue();
        PlaylistTrack current = state.Current!;

        state.MoveTrack(3, 0).Should().BeTrue();

        state.Tracks.Select(track => track.Name).Should().Equal("four", "one", "two", "three");
        state.Current.Should().BeSameAs(current);
        state.CurrentIndex.Should().Be(2);
    }

    [Fact]
    public void Clear_RemovesTracks_AndClearsCurrent()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov"]);

        state.Clear();

        state.Tracks.Should().BeEmpty();
        state.CurrentIndex.Should().Be(-1);
        state.Current.Should().BeNull();
    }

    [Fact]
    public void FindTrackById_ReturnsCorrectTrack()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov", "C:\\Videos\\two.mov"]);
        Guid targetId = state.Tracks[1].Id;

        PlaylistTrack? result = state.FindTrackById(targetId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(targetId);
        result.Name.Should().Be("two");
    }

    [Fact]
    public void FindTrackById_ReturnsNull_ForUnknownId()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov"]);
        Guid unknownId = Guid.NewGuid();

        PlaylistTrack? result = state.FindTrackById(unknownId);

        result.Should().BeNull();
    }

    [Fact]
    public void FindIndexById_ReturnsCorrectIndex()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov", "C:\\Videos\\two.mov", "C:\\Videos\\three.mov"]);
        Guid targetId = state.Tracks[2].Id;

        int result = state.FindIndexById(targetId);

        result.Should().Be(2);
    }

    [Fact]
    public void FindIndexById_ReturnsMinusOne_ForUnknownId()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov"]);
        Guid unknownId = Guid.NewGuid();

        int result = state.FindIndexById(unknownId);

        result.Should().Be(-1);
    }

    [Fact]
    public void RemoveAt_WhenRemovingOnlyTrack_CountBecomesZero()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov"]);

        state.RemoveAt(0).Should().BeTrue();

        state.Tracks.Should().BeEmpty();
        state.CurrentIndex.Should().Be(-1);
        state.Current.Should().BeNull();
    }

    [Fact]
    public void MoveTrack_WithSameIndex_ReturnsFalse()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\one.mov", "C:\\Videos\\two.mov"]);
        PlaylistTrack current = state.Current!;

        state.MoveTrack(0, 0).Should().BeFalse();

        state.Tracks.Should().HaveCount(2);
        state.Tracks[0].Name.Should().Be("one");
        state.Current.Should().BeSameAs(current);
    }

    [Fact]
    public void AddTrack_AddsTrackCorrectly()
    {
        var state = new PlaylistState();

        state.AddTrack("C:\\Videos\\solo.mp4");

        state.Tracks.Should().ContainSingle();
        state.Tracks[0].FilePath.Should().Be("C:\\Videos\\solo.mp4");
        state.Tracks[0].Name.Should().Be("solo");
        state.CurrentIndex.Should().Be(0);
        state.Current.Should().BeSameAs(state.Tracks[0]);
    }
}
