using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class RenderFrameSizePolicyTests
{
    [Fact]
    public void Decide_ReturnsVideoSize_WhenWidthAndHeightArePositive()
    {
        RenderFrameSizeDecision result = RenderFrameSizePolicy.Decide(1920, 1080, fallbackSize: 16);

        result.Width.Should().Be(1920);
        result.Height.Should().Be(1080);
        result.HasDisplayableVideoSize.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, 1080)]
    public void Decide_ReturnsFallback_WhenSizeIsInvalid(int width, int height)
    {
        RenderFrameSizeDecision result = RenderFrameSizePolicy.Decide(width, height, fallbackSize: 16);

        result.Width.Should().Be(16);
        result.Height.Should().Be(16);
        result.HasDisplayableVideoSize.Should().BeFalse();
    }
}
