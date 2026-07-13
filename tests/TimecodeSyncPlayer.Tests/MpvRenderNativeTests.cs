using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class MpvRenderNativeTests
{
    [Fact]
    public void SoftwareRenderParameterConstants_MatchCurrentLibmpvRenderHeader()
    {
        MpvRenderNative.MPV_RENDER_PARAM_SW_SIZE.Should().Be(17);
        MpvRenderNative.MPV_RENDER_PARAM_SW_FORMAT.Should().Be(18);
        MpvRenderNative.MPV_RENDER_PARAM_SW_STRIDE.Should().Be(19);
        MpvRenderNative.MPV_RENDER_PARAM_SW_POINTER.Should().Be(20);
    }
}
