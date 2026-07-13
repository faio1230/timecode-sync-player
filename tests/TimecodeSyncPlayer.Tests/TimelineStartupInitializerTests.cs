using System.Windows;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class TimelineStartupInitializerTests
{
    [Theory]
    [InlineData(true, Visibility.Visible, "Timeline ON")]
    [InlineData(false, Visibility.Collapsed, "Timeline OFF")]
    public void CreateState_ReturnsVisibilityAndLabel(bool isVisible, Visibility visibility, string label)
    {
        TimelineStartupInitializer.CreateState(isVisible).Should().Be(new TimelineStartupState(visibility, label));
    }
}
