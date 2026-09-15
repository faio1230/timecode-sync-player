using FluentAssertions;
using TimecodeSyncPlayer;

namespace TimecodeSyncPlayer.Tests;

public class DecodeModePolicyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("hardware", false)]
    [InlineData("HARDWARE", false)]
    [InlineData("software", true)]
    [InlineData("Software", true)]
    [InlineData("bogus", false)]
    public void Resolve_MapsValues(string? value, bool software)
    {
        DecodeMode expected = software ? DecodeMode.Software : DecodeMode.Hardware;
        DecodeModePolicy.Resolve(value).Should().Be(expected);
    }

    [Fact]
    public void Resolve_UnknownValue_WarnsOnce()
    {
        int warnings = 0;

        DecodeModePolicy.Resolve("sofware", _ => warnings++).Should().Be(DecodeMode.Hardware);

        warnings.Should().Be(1);
    }

    [Fact]
    public void Resolve_KnownAndEmptyValues_DoNotWarn()
    {
        int warnings = 0;

        DecodeModePolicy.Resolve("software", _ => warnings++);
        DecodeModePolicy.Resolve("hardware", _ => warnings++);
        DecodeModePolicy.Resolve(null, _ => warnings++);

        warnings.Should().Be(0);
    }
}
