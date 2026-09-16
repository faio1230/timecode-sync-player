using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D18: 同梱 GStreamer（exe と同じディレクトリの gstreamer\bin）と開発環境
/// （GSTREAMER_1_0_ROOT_MSVC_X86_64）の両経路を固定する。
/// </summary>
public sealed class GstRuntimeLocatorTests
{
    [Fact]
    public void GstRuntimeBinDirectory_UsesBundledRuntimeNextToExe()
    {
        string root = NewTempDirectory();
        try
        {
            string exeDir = Path.Combine(root, "app");
            string bin = Path.Combine(exeDir, "gstreamer", "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllBytes(Path.Combine(bin, "gstreamer-1.0-0.dll"), Array.Empty<byte>());

            E2EAppRunner.GstRuntimeBinDirectory(exeDir, environmentRoot: null)
                .Should().Be(bin);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GstRuntimeBinDirectory_UsesEnvironmentRootWhenNothingIsBundled()
    {
        string root = NewTempDirectory();
        try
        {
            string exeDir = Path.Combine(root, "app");
            Directory.CreateDirectory(exeDir);
            string environmentRoot = Path.Combine(root, "gstreamer-root");
            string bin = Path.Combine(environmentRoot, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllBytes(Path.Combine(bin, "gstreamer-1.0-0.dll"), Array.Empty<byte>());

            E2EAppRunner.GstRuntimeBinDirectory(exeDir, environmentRoot)
                .Should().Be(bin);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(), "tcs-gst-locator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
