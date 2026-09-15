using FluentAssertions;
using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer.Tests;

public class GstRootResolutionTests
{
    private const string EnvRoot = @"C:\env\gstreamer";
    private const string BundledRoot = @"C:\app\gstreamer";
    private const string ProgramFilesRoot = @"C:\Program Files\gstreamer\1.0\msvc_x86_64";

    [Fact]
    public void ResolveRoot_EnvironmentVariable_WinsOverEverything()
    {
        GstRoot? root = GstNativeLibraryResolver.ResolveRoot(
            EnvRoot, BundledRoot, ProgramFilesRoot, _ => true);

        root.Should().Be(new GstRoot(EnvRoot, GstRootSource.EnvironmentVariable));
    }

    [Fact]
    public void ResolveRoot_BundledRuntime_WinsOverProgramFiles()
    {
        GstRoot? root = GstNativeLibraryResolver.ResolveRoot(
            null, BundledRoot, ProgramFilesRoot, path => path != ProgramFilesRoot);

        root.Should().Be(new GstRoot(BundledRoot, GstRootSource.Bundled));
    }

    [Fact]
    public void ResolveRoot_NoBundledRuntime_FallsBackToProgramFiles()
    {
        GstRoot? root = GstNativeLibraryResolver.ResolveRoot(
            null, BundledRoot, ProgramFilesRoot, path => path == ProgramFilesRoot);

        root.Should().Be(new GstRoot(ProgramFilesRoot, GstRootSource.ProgramFiles));
    }

    [Fact]
    public void ResolveRoot_InvalidEnvironmentVariable_IsIgnored()
    {
        GstRoot? root = GstNativeLibraryResolver.ResolveRoot(
            EnvRoot, BundledRoot, ProgramFilesRoot, path => path == BundledRoot);

        root.Should().Be(new GstRoot(BundledRoot, GstRootSource.Bundled));
    }

    [Fact]
    public void ResolveRoot_NothingFound_ReturnsNull()
    {
        GstNativeLibraryResolver.ResolveRoot(null, BundledRoot, ProgramFilesRoot, _ => false)
            .Should().BeNull();
    }

    [Fact]
    public void ResolveRoot_EmptyEnvironmentVariable_IsIgnored()
    {
        GstNativeLibraryResolver.ResolveRoot("", BundledRoot, ProgramFilesRoot, path => path == ProgramFilesRoot)
            .Should().Be(new GstRoot(ProgramFilesRoot, GstRootSource.ProgramFiles));
    }
}
