using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class BlackFrameRenderPolicyTests
{
    [Fact]
    public void ResolveSize_UsesFallbackSize_WhenVideoSizeIsUnknown()
    {
        var result = BlackFrameRenderPolicy.ResolveSize(videoWidth: 0, videoHeight: 0);

        result.Width.Should().Be(16);
        result.Height.Should().Be(16);
    }

    [Fact]
    public void ResolveSize_UsesVideoSize_WhenVideoSizeIsAvailable()
    {
        var result = BlackFrameRenderPolicy.ResolveSize(videoWidth: 1920, videoHeight: 1080);

        result.Width.Should().Be(1920);
        result.Height.Should().Be(1080);
    }
}
