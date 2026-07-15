using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class MpvLibraryNameResolverTests
{
    [Fact]
    public void GetCandidates_ForImportedName_ReturnsCompatibilityThenUpstreamName()
    {
        IReadOnlyList<string> candidates =
            MpvLibraryNameResolver.GetCandidates(MpvLibraryNameResolver.ImportedLibraryName);

        candidates.Should().Equal("mpv-2.dll", "libmpv-2.dll");
    }

    [Fact]
    public void GetCandidates_ForOtherLibrary_ReturnsEmpty()
    {
        MpvLibraryNameResolver.GetCandidates("SpoutDX.dll").Should().BeEmpty();
    }

    [Fact]
    public void GetCandidates_IsCaseSensitiveToExactImportName()
    {
        MpvLibraryNameResolver.GetCandidates("MPV-2.DLL").Should().BeEmpty();
    }
}
