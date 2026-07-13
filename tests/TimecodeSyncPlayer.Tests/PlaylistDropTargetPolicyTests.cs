using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistDropTargetPolicyTests
{
    [Fact]
    public void ResolveTargetIndex_ReturnsHitIndex_WhenHitIndexIsValid()
    {
        PlaylistDropTargetPolicy.ResolveTargetIndex(hitIndex: 1, trackCount: 3).Should().Be(1);
    }

    [Fact]
    public void ResolveTargetIndex_UsesLastIndex_WhenDroppedOutsideItems()
    {
        PlaylistDropTargetPolicy.ResolveTargetIndex(hitIndex: -1, trackCount: 3).Should().Be(2);
    }

    [Fact]
    public void ResolveTargetIndex_ReturnsMinusOne_WhenPlaylistIsEmpty()
    {
        PlaylistDropTargetPolicy.ResolveTargetIndex(hitIndex: -1, trackCount: 0).Should().Be(-1);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(99)]
    public void ResolveTargetIndex_ClampsOversizedHitIndexToLastIndex(int hitIndex)
    {
        PlaylistDropTargetPolicy.ResolveTargetIndex(hitIndex, trackCount: 3).Should().Be(2);
    }
}
