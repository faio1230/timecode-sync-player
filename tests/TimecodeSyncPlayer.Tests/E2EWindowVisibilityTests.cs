using System.Drawing;
using FlaUI.Core.Exceptions;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public sealed class E2EWindowVisibilityTests
{
    [Fact]
    public void IsVisible_WhenIsOffscreenIsFalse_ReturnsTrue()
    {
        bool visible = E2EWindowVisibility.IsVisible(
            isOffscreen: () => false,
            boundingRectangle: () => Rectangle.Empty);

        visible.Should().BeTrue();
    }

    [Fact]
    public void IsVisible_WhenIsOffscreenIsTrue_ReturnsFalse()
    {
        bool visible = E2EWindowVisibility.IsVisible(
            isOffscreen: () => true,
            boundingRectangle: () => new Rectangle(10, 10, 800, 600));

        visible.Should().BeFalse();
    }

    [Fact]
    public void IsVisible_WhenIsOffscreenIsUnsupportedAndBoundsArePositive_ReturnsTrue()
    {
        bool visible = E2EWindowVisibility.IsVisible(
            isOffscreen: () => throw new PropertyNotSupportedException(),
            boundingRectangle: () => new Rectangle(10, 10, 800, 600));

        visible.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(800, 0)]
    [InlineData(-1, 600)]
    [InlineData(800, -1)]
    public void IsVisible_WhenIsOffscreenIsUnsupportedAndBoundsAreNotPositive_ReturnsFalse(
        int width,
        int height)
    {
        bool visible = E2EWindowVisibility.IsVisible(
            isOffscreen: () => throw new PropertyNotSupportedException(),
            boundingRectangle: () => new Rectangle(10, 10, width, height));

        visible.Should().BeFalse();
    }
}
