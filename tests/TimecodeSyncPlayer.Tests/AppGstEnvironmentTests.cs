using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.E2E;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// 試験基盤の 9: アプリに渡す環境の組み立て。exe の隣に同梱の gstreamer\bin があるときだけ、
/// GSTREAMER_1_0_ROOT_MSVC_X86_64 と PATH の GStreamer の bin を外し、アプリを同梱の GStreamer だけで起動させる。
/// 同梱が無いとき（開発機の Debug の exe）は触らない。
/// </summary>
public sealed class AppGstEnvironmentTests
{
    private const string ExeDir = @"C:\app";
    private const string BundledBin = @"C:\app\gstreamer\bin";
    private const string InstalledRoot = @"C:\gstreamer\1.0\msvc_x86_64";
    private const string InstalledBin = @"C:\gstreamer\1.0\msvc_x86_64\bin";
    private const string OtherDir = @"C:\Windows\System32";
    private const string FfmpegDir = @"C:\tools\ffmpeg";

    private static Func<string, bool> GstBins(params string[] bins) =>
        dir => bins.Any(bin => string.Equals(dir.TrimEnd('\\'), bin, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, string?> Env(string? root, params string[] path)
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = string.Join(Path.PathSeparator, path),
            ["OTHER"] = "kept",
        };
        if (root != null) env[E2EAppRunner.GstRootVariable] = root;
        return env;
    }

    [Fact]
    public void Bundled_RemovesRootVariableAndGstBinFromPath()
    {
        var env = Env(InstalledRoot, InstalledBin, OtherDir);

        AppGstEnvironment result = E2EAppRunner.ApplyProductionGstEnvironment(
            env, ExeDir, callerSetRoot: false, GstBins(BundledBin, InstalledBin));

        env.Should().NotContainKey(E2EAppRunner.GstRootVariable);
        env["PATH"].Should().Be(OtherDir);
        env["OTHER"].Should().Be("kept");
        result.Should().Be(new AppGstEnvironment(true, null, true, 1, 0));
        result.Describe().Should().Be(
            "app-env: bundledGstreamer=yes GSTREAMER_1_0_ROOT_MSVC_X86_64=<not passed> (removed=yes) pathGstBinEntries=0 (removed=1)");
    }

    [Fact]
    public void Bundled_RemovesTheBundledBinItselfFromPath()
    {
        // 検証機では展開先の gstreamer\bin（= 同梱）を試験のプロセスの PATH の先頭に置く。本番の PATH には無い。
        var env = Env(null, BundledBin, OtherDir);

        AppGstEnvironment result = E2EAppRunner.ApplyProductionGstEnvironment(
            env, ExeDir, callerSetRoot: false, GstBins(BundledBin));

        env["PATH"].Should().Be(OtherDir);
        result.Should().Be(new AppGstEnvironment(true, null, false, 1, 0));
    }

    [Fact]
    public void Bundled_WithoutVariableOrGstPath_ChangesNothing()
    {
        var env = Env(null, OtherDir, FfmpegDir);

        AppGstEnvironment result = E2EAppRunner.ApplyProductionGstEnvironment(
            env, ExeDir, callerSetRoot: false, GstBins(BundledBin));

        env["PATH"].Should().Be(OtherDir + Path.PathSeparator + FfmpegDir);
        env.Should().NotContainKey(E2EAppRunner.GstRootVariable);
        result.Should().Be(new AppGstEnvironment(true, null, false, 0, 0));
    }

    [Fact]
    public void Bundled_KeepsRootVariableTheCallerSetExplicitly()
    {
        var env = Env(InstalledRoot, InstalledBin, OtherDir);

        AppGstEnvironment result = E2EAppRunner.ApplyProductionGstEnvironment(
            env, ExeDir, callerSetRoot: true, GstBins(BundledBin, InstalledBin));

        env[E2EAppRunner.GstRootVariable].Should().Be(InstalledRoot);
        env["PATH"].Should().Be(OtherDir);
        result.Should().Be(new AppGstEnvironment(true, InstalledRoot, false, 1, 0));
    }

    [Fact]
    public void NotBundled_KeepsRootVariableAndPath()
    {
        // 開発機の Debug の exe: 同梱が無く、アプリは変数か Program Files の GStreamer しか使えない。
        var env = Env(InstalledRoot, InstalledBin, OtherDir);

        AppGstEnvironment result = E2EAppRunner.ApplyProductionGstEnvironment(
            env, ExeDir, callerSetRoot: false, GstBins(InstalledBin));

        env[E2EAppRunner.GstRootVariable].Should().Be(InstalledRoot);
        env["PATH"].Should().Be(InstalledBin + Path.PathSeparator + OtherDir);
        result.Should().Be(new AppGstEnvironment(false, InstalledRoot, false, 0, 1));
        result.Describe().Should().Be(
            $"app-env: bundledGstreamer=no GSTREAMER_1_0_ROOT_MSVC_X86_64={InstalledRoot} (removed=no) pathGstBinEntries=1 (removed=0)");
    }

    [Fact]
    public void NotBundled_WithoutVariable_ChangesNothing()
    {
        var env = Env(null, OtherDir);

        AppGstEnvironment result = E2EAppRunner.ApplyProductionGstEnvironment(
            env, ExeDir, callerSetRoot: false, GstBins(InstalledBin));

        env["PATH"].Should().Be(OtherDir);
        result.Should().Be(new AppGstEnvironment(false, null, false, 0, 0));
    }

    [Fact]
    public void PathForApp_StripsGstBinOnlyWhenBundled()
    {
        string path = string.Join(Path.PathSeparator, $"\"{InstalledBin}\"", OtherDir, "");

        E2EAppRunner.PathForApp(path, ExeDir, GstBins(BundledBin, InstalledBin))
            .Should().Be(OtherDir + Path.PathSeparator);
        E2EAppRunner.PathForApp(path, ExeDir, GstBins(InstalledBin)).Should().Be(path);
    }

    [Fact]
    public void PathWithoutFfmpeg_AlsoStripsGstBinWhenTheExeHasTheBundledRuntime()
    {
        string root = Path.Combine(Path.GetTempPath(), "tcs-app-env-" + Guid.NewGuid().ToString("N"));
        try
        {
            string exeDir = Path.Combine(root, "app");
            string bundledBin = Path.Combine(exeDir, "gstreamer", "bin");
            string installedBin = Path.Combine(root, "gst", "bin");
            string ffmpegDir = Path.Combine(root, "ffmpeg");
            string otherDir = Path.Combine(root, "other");
            foreach (string dir in new[] { bundledBin, installedBin, ffmpegDir, otherDir })
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(installedBin, "gstreamer-1.0-0.dll"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(ffmpegDir, "ffprobe.exe"), Array.Empty<byte>());
            string path = string.Join(Path.PathSeparator, installedBin, ffmpegDir, otherDir);

            // 同梱なし: ffmpeg のフォルダだけ外す（GStreamer の bin は残す）。
            F7DurationWithoutFfprobeE2ETests.PathWithoutFfmpeg(path, exeDir)
                .Should().Be(installedBin + Path.PathSeparator + otherDir);

            // 同梱あり: GStreamer の bin も外す。
            File.WriteAllBytes(Path.Combine(bundledBin, "gstreamer-1.0-0.dll"), Array.Empty<byte>());
            F7DurationWithoutFfprobeE2ETests.PathWithoutFfmpeg(path, exeDir).Should().Be(otherDir);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"C:\app\gstreamer\bin\gstreamer-1.0-0.dll", null, "bundled")]
    [InlineData(@"C:\APP\GStreamer\bin\gstreamer-1.0-0.dll", null, "bundled")]
    [InlineData(@"C:\gstreamer\1.0\msvc_x86_64\bin\gstreamer-1.0-0.dll", InstalledRoot, "environment")]
    [InlineData(@"C:\Program Files\gstreamer\1.0\msvc_x86_64\bin\gstreamer-1.0-0.dll", null, "programFiles")]
    [InlineData(@"C:\gstreamer\1.0\msvc_x86_64\bin\gstreamer-1.0-0.dll", null, "other")]
    [InlineData(null, null, "none")]
    public void ClassifyGstOrigin_NamesWhereTheLoadedDllCameFrom(string? loaded, string? rootPassed, string expected)
    {
        E2EAppRunner.ClassifyGstOrigin(loaded, ExeDir, rootPassed,
                @"C:\Program Files\gstreamer\1.0\msvc_x86_64\bin")
            .Should().Be(expected);
    }
}
