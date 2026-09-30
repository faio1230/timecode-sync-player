using System.IO;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>段 5b: 試験の ffmpeg の解決の順（TCS_FFMPEG → PATH、ffprobe は TCS_FFPROBE → 同じフォルダ → PATH）と版の読み取り。</summary>
public sealed class FfmpegToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tcs-ffmpeg-tool-" + Guid.NewGuid().ToString("N"));

    public FfmpegToolTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string MakeFile(string folder, string name)
    {
        string directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, "");
        return path;
    }

    private static Func<string, string?> Env(string? ffmpeg = null, string? ffprobe = null) => name => name switch
    {
        FfmpegTool.FfmpegEnvironmentVariable => ffmpeg,
        FfmpegTool.FfprobeEnvironmentVariable => ffprobe,
        _ => null,
    };

    [Fact]
    public void Resolve_EnvironmentWinsOverDefaultAndPath_AndFfprobeComesFromTheSameFolder()
    {
        string pinned = MakeFile("pinned", "ffmpeg.exe");
        string pinnedProbe = MakeFile("pinned", "ffprobe.exe");
        MakeFile("default", "ffmpeg.exe");
        MakeFile("default", "ffprobe.exe");
        MakeFile("path", "ffmpeg.exe");
        MakeFile("path", "ffprobe.exe");

        FfmpegResolution resolution = FfmpegTool.Resolve(Env(ffmpeg: pinned), Path.Combine(_root, "default"), Path.Combine(_root, "path"));

        Assert.Equal(pinned, resolution.Ffmpeg);
        Assert.Equal("env:TCS_FFMPEG", resolution.FfmpegSource);
        Assert.Equal(pinnedProbe, resolution.Ffprobe);
        Assert.Equal("next-to-ffmpeg", resolution.FfprobeSource);
    }

    [Fact]
    public void Resolve_WithoutEnvironment_DefaultFolderWinsOverPath()
    {
        string preset = MakeFile("default", "ffmpeg.exe");
        string presetProbe = MakeFile("default", "ffprobe.exe");
        MakeFile("path", "ffmpeg.exe");

        FfmpegResolution resolution = FfmpegTool.Resolve(Env(), Path.Combine(_root, "default"), Path.Combine(_root, "path"));

        Assert.Equal(preset, resolution.Ffmpeg);
        Assert.Equal("default-dir", resolution.FfmpegSource);
        Assert.Equal(presetProbe, resolution.Ffprobe);
        Assert.Equal("next-to-ffmpeg", resolution.FfprobeSource);
    }

    [Fact]
    public void Resolve_WithoutEnvironmentAndDefaultFolder_UsesTheFirstPathEntry()
    {
        MakeFile("old", "ffmpeg.exe");
        string newer = MakeFile("new", "ffmpeg.exe");
        string newerProbe = MakeFile("new", "ffprobe.exe");
        string path = string.Join(Path.PathSeparator, Path.Combine(_root, "old"), Path.Combine(_root, "new"));

        FfmpegResolution resolution = FfmpegTool.Resolve(Env(), Path.Combine(_root, "no-default"), path);

        // ffmpeg は PATH の先頭（old）。old に ffprobe が無いので ffprobe は PATH から（new）。
        Assert.Equal(Path.Combine(_root, "old", "ffmpeg.exe"), resolution.Ffmpeg);
        Assert.Equal("PATH", resolution.FfmpegSource);
        Assert.Equal(newerProbe, resolution.Ffprobe);
        Assert.Equal("PATH", resolution.FfprobeSource);
        Assert.NotEqual(newer, resolution.Ffmpeg);
    }

    [Fact]
    public void Resolve_FfprobeEnvironmentWinsOverTheFfmpegFolder()
    {
        string pinned = MakeFile("pinned", "ffmpeg.exe");
        MakeFile("pinned", "ffprobe.exe");
        string probe = MakeFile("probe", "ffprobe.exe");

        FfmpegResolution resolution = FfmpegTool.Resolve(Env(ffmpeg: pinned, ffprobe: probe), null, null);

        Assert.Equal(probe, resolution.Ffprobe);
        Assert.Equal("env:TCS_FFPROBE", resolution.FfprobeSource);
    }

    [Fact]
    public void Resolve_EnvironmentPointingNowhere_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            FfmpegTool.Resolve(Env(ffmpeg: Path.Combine(_root, "missing", "ffmpeg.exe")), null, null));

    [Fact]
    public void Resolve_NothingFound_FallsBackToTheNames()
    {
        FfmpegResolution resolution = FfmpegTool.Resolve(Env(), Path.Combine(_root, "no-default"), Path.Combine(_root, "empty"));
        Assert.Equal("ffmpeg", resolution.Ffmpeg);
        Assert.Equal("ffprobe", resolution.Ffprobe);
    }

    [Theory]
    [InlineData("ffmpeg version 4.2.3 Copyright (c) 2000-2020 the FFmpeg developers\nlibavcodec     58. 54.100 / 58. 54.100", 4)]
    [InlineData("ffmpeg version n5.0 Copyright (c) 2000-2022 the FFmpeg developers", 5)]
    [InlineData("ffmpeg version N-109850-g78f46065d8-20230212 Copyright (c) 2000-2023 the FFmpeg developers\r\nlibavutil      58.  0.100 / 58.  0.100\r\nlibavcodec     60.  1.100 / 60.  1.100", 6)]
    [InlineData("ffmpeg version 8.0.1-full_build-www.gyan.dev Copyright (c) 2000-2025 the FFmpeg developers", 8)]
    [InlineData("ffmpeg -version failed: not found", -1)]
    public void ParseMajor_ReadsReleaseOrLibavcodec(string output, int expected) =>
        Assert.Equal(expected, FfmpegTool.ParseMajor(output));

    [Theory]
    [InlineData(4, true)]
    [InlineData(-1, true)]
    [InlineData(6, false)]
    [InlineData(8, false)]
    public void OldVersionWarning_OnlyBelowSix(int major, bool warns) =>
        Assert.Equal(warns, FfmpegTool.OldVersionWarning("ffmpeg version x", major) is not null);

    [Fact]
    public void WriteSidecar_KeepsOtherLinesAndReplacesTheRemadeOnes()
    {
        string first = FfmpegTool.WriteSidecar(_root, ["clip-1.mp4", "clip-2.mp4"], "ffmpeg version 4.2.3");
        FfmpegTool.WriteSidecar(_root, ["clip-2.mp4"], "ffmpeg version 8.0.1");

        string[] lines = File.ReadAllLines(first);
        Assert.StartsWith("#", lines[0]);
        Assert.StartsWith("clip-1.mp4 | ffmpeg version 4.2.3 | made ", lines[1]);
        Assert.StartsWith("clip-2.mp4 | ffmpeg version 8.0.1 | made ", lines[2]);
        Assert.Equal(3, lines.Length);
    }
}
