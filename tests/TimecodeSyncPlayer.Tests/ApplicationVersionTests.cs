using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class ApplicationVersionTests
{
    [Fact]
    public void Normalize_UsesInformationalVersionWithoutBuildMetadata()
    {
        ApplicationVersion.Normalize("0.1.0+abcdef", new Version(9, 9, 9, 9))
            .Should().Be("0.1.0");
    }

    [Fact]
    public void Normalize_UsesThreePartAssemblyVersionAsFallback()
    {
        ApplicationVersion.Normalize(null, new Version(1, 2, 3, 4))
            .Should().Be("1.2.3");
    }

    [Fact]
    public void CurrentVersionAndWindowTitle_ComeFromApplicationAssembly()
    {
        ApplicationVersion.Current.Should().Be("0.1.0");
        ApplicationVersion.WindowTitle.Should().Be("Timecode Sync Player v0.1.0");
    }
}
