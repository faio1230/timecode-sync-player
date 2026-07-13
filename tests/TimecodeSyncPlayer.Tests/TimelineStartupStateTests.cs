using System.Windows;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class TimelineStartupStateTests
{
    [Theory]
    [InlineData(true, Visibility.Visible, "Timeline ON")]
    [InlineData(false, Visibility.Collapsed, "Timeline OFF")]
    public void FromVisibility_ReturnsContainerVisibilityAndLabel(
        bool isVisible,
        Visibility expectedVisibility,
        string expectedLabel)
    {
        TimelineStartupState state = TimelineStartupState.FromVisibility(isVisible);

        state.ContainerVisibility.Should().Be(expectedVisibility);
        state.ToggleLabel.Should().Be(expectedLabel);
    }
}
