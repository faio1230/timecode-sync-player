using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>段階 4.2/4.5: 現在クリップの Fit を ClipPlacement に写す。</summary>
public class TimelineOutputStateTests
{
    private static PlaylistTrack TrackWithFit(string? fit) => new(
        Id: Guid.NewGuid(),
        FilePath: "clip.mp4",
        Name: "clip",
        MediaIn: TimeSpan.Zero,
        MediaOut: null,
        TimelineOffset: TimeSpan.Zero,
        MediaDuration: TimeSpan.FromSeconds(10),
        SyncOffset: TimeSpan.Zero,
        FrameRate: 30,
        IsEnabled: true,
        Fit: fit);

    [Fact]
    public void PlacementFor_NoTrack_InheritsProjectDefault()
    {
        TimelineOutputState.PlacementFor(null).FitId.Should().BeNull();
    }

    [Fact]
    public void PlacementFor_TrackWithoutFit_InheritsProjectDefault()
    {
        TimelineOutputState.PlacementFor(TrackWithFit(null)).FitId.Should().BeNull();
    }

    [Theory]
    [InlineData(FitHeight.FitId)]
    [InlineData(FitWidth.FitId)]
    public void PlacementFor_TrackFit_IsCopiedToClipPlacement(string fit)
    {
        TimelineOutputState.PlacementFor(TrackWithFit(fit)).FitId.Should().Be(fit);
    }
}
