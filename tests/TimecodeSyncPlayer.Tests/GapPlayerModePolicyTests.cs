using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class GapPlayerModePolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pause")]
    [InlineData("PAUSE")]
    public void Resolve_DefaultsToPause(string? value)
    {
        string? warned = null;

        GapPlayerModePolicy.Resolve(value, v => warned = v).Should().Be(GapPlayerMode.Pause);
        warned.Should().BeNull();
    }

    [Theory]
    [InlineData("compose-black")]
    [InlineData("Compose-Black")]
    [InlineData("COMPOSE-BLACK")]
    public void Resolve_ComposeBlack(string value) =>
        GapPlayerModePolicy.Resolve(value).Should().Be(GapPlayerMode.ComposeBlack);

    [Fact]
    public void Resolve_UnknownValueWarnsOnceAndFallsBack()
    {
        string? warned = null;

        GapPlayerModePolicy.Resolve("fast", v => warned = v).Should().Be(GapPlayerMode.Pause);
        warned.Should().Be("fast");
    }

    [Theory]
    [InlineData(0, "pause")]
    [InlineData(1, "compose-black")]
    public void Describe_ReturnsEnvironmentValue(int modeValue, string expected) =>
        GapPlayerModePolicy.Describe((GapPlayerMode)modeValue).Should().Be(expected);
}
