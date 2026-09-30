using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistAddSelectionPolicyTests
{
    [Fact]
    public void ShouldSyncSelection_WhenNothingIsSelected_ReturnsTrue()
    {
        PlaylistAddSelectionPolicy.ShouldSyncSelection(-1).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public void ShouldSyncSelection_WhenARowIsSelected_ReturnsFalse(int selectedIndex)
    {
        PlaylistAddSelectionPolicy.ShouldSyncSelection(selectedIndex).Should().BeFalse();
    }
}
