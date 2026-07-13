using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class SpoutOutputTests
{
    [Theory]
    [InlineData(1, 4)]
    [InlineData(1280, 5120)]
    [InlineData(1920, 7680)]
    public void GetTightlyPackedBgraPitch_ReturnsWidthTimesFour(int width, uint expected)
    {
        SpoutOutput.GetTightlyPackedBgraPitch(width).Should().Be(expected);
    }
}
