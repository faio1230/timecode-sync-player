using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistDragDropCoordinatorTests
{
    [Fact]
    public void HandleMouseMove_BelowThresholdDoesNotBeginDrag()
    {
        var calls = new List<string>();
        var coordinator = CreateCoordinator(calls);
        coordinator.SetDragStart(10, 20);

        coordinator.HandleMouseMove(
            Track(),
            currentX: 13.9,
            currentY: 23.9,
            isLeftButtonPressed: true,
            minimumHorizontalDistance: 4,
            minimumVerticalDistance: 4);

        calls.Should().BeEmpty();
    }

    [Fact]
    public void HandleMouseMove_AtThresholdBeginsDragAndClearsStartPoint()
    {
        var calls = new List<string>();
        PlaylistTrack track = Track();
        var coordinator = CreateCoordinator(calls);
        coordinator.SetDragStart(10, 20);

        coordinator.HandleMouseMove(track, 14, 20, true, 4, 4);
        coordinator.HandleMouseMove(track, 20, 20, true, 4, 4);

        calls.Should().Equal($"begin:{track.Id}");
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void HandleMouseMove_MissingPrerequisiteDoesNotBeginDrag(
        bool setStart,
        bool pressed,
        bool hasTrack)
    {
        var calls = new List<string>();
        var coordinator = CreateCoordinator(calls);
        if (setStart) coordinator.SetDragStart(0, 0);

        coordinator.HandleMouseMove(hasTrack ? Track() : null, 10, 10, pressed, 4, 4);

        calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CanAcceptDrop_MatchesPlaylistTrackDataPresence(bool hasData)
    {
        PlaylistDragDropCoordinator.CanAcceptDrop(hasData).Should().Be(hasData);
    }

    [Fact]
    public void HandleDrop_NullTrackReturnsFalseWithoutEffects()
    {
        var calls = new List<string>();
        var coordinator = CreateCoordinator(calls);

        coordinator.HandleDrop(null, 0).Should().BeFalse();

        calls.Should().BeEmpty();
    }

    [Fact]
    public void HandleDrop_EmptyPlaylistReturnsFalseBeforeMove()
    {
        var calls = new List<string>();
        var coordinator = CreateCoordinator(calls, trackCount: 0);

        coordinator.HandleDrop(Track(), -1).Should().BeFalse();

        calls.Should().Equal("index-of", "count");
    }

    [Fact]
    public void HandleDrop_MoveFailureReturnsFalseWithoutUiUpdates()
    {
        var calls = new List<string>();
        var coordinator = CreateCoordinator(calls, moveResult: false);

        coordinator.HandleDrop(Track(), 1).Should().BeFalse();

        calls.Should().Equal("index-of", "count", "move:0:1");
    }

    [Fact]
    public void HandleDrop_MoveSuccessUpdatesSelectionLabelAndTimelineInOrder()
    {
        var calls = new List<string>();
        var coordinator = CreateCoordinator(calls, trackCount: 3, moveResult: true);

        coordinator.HandleDrop(Track(), -1).Should().BeTrue();

        calls.Should().Equal(
            "index-of",
            "count",
            "move:0:2",
            "select:2",
            "label",
            "timeline");
    }

    private static PlaylistDragDropCoordinator CreateCoordinator(
        List<string> calls,
        int trackCount = 3,
        bool moveResult = true)
    {
        return new PlaylistDragDropCoordinator(new PlaylistDragDropEffects(
            BeginDrag: track => calls.Add($"begin:{track.Id}"),
            IndexOf: _ =>
            {
                calls.Add("index-of");
                return 0;
            },
            GetTrackCount: () =>
            {
                calls.Add("count");
                return trackCount;
            },
            MoveTrack: (from, to) =>
            {
                calls.Add($"move:{from}:{to}");
                return moveResult;
            },
            SetSelectedIndex: index => calls.Add($"select:{index}"),
            UpdateCurrentTrackLabel: () => calls.Add("label"),
            UpdatePlaylistTimelineDisplay: () => calls.Add("timeline")));
    }

    private static PlaylistTrack Track() =>
        new(
            Guid.NewGuid(),
            "test.mp4",
            "Test",
            MediaIn: TimeSpan.Zero,
            MediaOut: null,
            TimelineOffset: TimeSpan.Zero,
            MediaDuration: TimeSpan.FromSeconds(10),
            SyncOffset: TimeSpan.Zero,
            FrameRate: null,
            IsEnabled: true);
}
