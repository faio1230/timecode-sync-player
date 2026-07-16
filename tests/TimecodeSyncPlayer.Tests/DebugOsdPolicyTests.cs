using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class DebugOsdPolicyTests
{
    [Fact]
    public void ShouldWrite_WhenSettingIsDisabled_ReturnsFalse()
    {
        DebugOsdPolicy.ShouldWrite(showDebugOsd: false).Should().BeFalse();
    }

    [Fact]
    public void ShouldWrite_WhenSettingIsEnabled_ReturnsTrue()
    {
        DebugOsdPolicy.ShouldWrite(showDebugOsd: true).Should().BeTrue();
    }

    [Fact]
    public void FormatText_AppendsMetadataToTimecode()
    {
        DebugOsdPolicy.FormatText("0:00:12:15", "1920x1080  25.000 fps")
            .Should().Be("0:00:12:15\n1920x1080  25.000 fps");
    }
}
