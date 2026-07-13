using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ToggleLabelFormatterTests
{
    [Fact]
    public void Format_ReturnsOnLabel_WhenEnabled()
    {
        ToggleLabelFormatter.Format(true, "ON", "OFF").Should().Be("ON");
    }

    [Fact]
    public void Format_ReturnsOffLabel_WhenDisabled()
    {
        ToggleLabelFormatter.Format(false, "ON", "OFF").Should().Be("OFF");
    }
}
