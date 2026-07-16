using System.Windows.Input;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class FullscreenInputPolicyTests
{
    [Fact]
    public void ShouldClose_WhenEscapeIsPressed_ReturnsTrue()
    {
        FullscreenInputPolicy.ShouldClose(Key.Escape).Should().BeTrue();
    }

    [Theory]
    [InlineData(Key.Enter)]
    [InlineData(Key.Space)]
    [InlineData(Key.F11)]
    public void ShouldClose_WhenAnotherKeyIsPressed_ReturnsFalse(Key key)
    {
        FullscreenInputPolicy.ShouldClose(key).Should().BeFalse();
    }
}
