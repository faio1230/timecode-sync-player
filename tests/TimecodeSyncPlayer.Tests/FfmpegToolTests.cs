using System.IO;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>段 5b と試験基盤の 8: 試験の ffmpeg の解決の順（TCS_FFMPEG → tools\ffmpeg → 既定 → PATH、ffprobe は TCS_FFPROBE → 同じフォルダ → PATH）と版の読み取り。</summary>
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

        FfmpegResolution resolution = FfmpegTool.Resolve(Env(ffmpeg: pinned), Path.Combine(_root, "tools"), Path.Combine(_root, "default"), Path.Combine(_root, "path"));

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

        FfmpegResolution resolution = FfmpegTool.Resolve(Env(), Path.Combine(_root, "no-tools"), Path.Combine(_root, "default"), Path.Combine(_root, "path"));

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

        FfmpegResolution resolution = FfmpegTool.Resolve(Env(), Path.Combine(_root, "no-tools"), Path.Combine(_root, "no-default"), path);

        // ffmpeg は PATH の先頭（old）。old に ffprobe が無いので ffprobe は PATH から（new）。
        Assert.Equal(Path.Combine(_root, "old", "ffmpeg.exe"), resolution.Ffmpeg);
        Assert.Equal("PATH", resolution.FfmpegSource);
        Assert.Equal(newerProbe, resolution.Ffprobe);
        Assert.Equal("PATH", resolution.FfprobeSource);
        Assert.NotEqual(newer, resolution.Ffmpeg);
    }

    // ---- 試験基盤の 8: 解決の順（TCS_FFMPEG あり・tools\ffmpeg だけ・Program Files だけ・PATH だけ） ----

    private (string Tools, string Default, string Path) MakeAllFour()
    {
        foreach (string folder in new[] { "env", "tools", "default", "path" })
        {
            MakeFile(folder, "ffmpeg.exe");
            MakeFile(folder, "ffprobe.exe");
        }
        return (Path.Combine(_root, "tools"), Path.Combine(_root, "default"), Path.Combine(_root, "path"));
    }

    [Fact]
    public void Resolve_Item8_EnvironmentWinsOverToolsDefaultAndPath()
    {
        var (tools, preset, path) = MakeAllFour();
        string pinned = Path.Combine(_root, "env", "ffmpeg.exe");

        FfmpegResolution resolution = FfmpegTool.Resolve(Env(ffmpeg: pinned), tools, preset, path);

        Assert.Equal(pinned, resolution.Ffmpeg);
        Assert.Equal("env:TCS_FFMPEG", resolution.FfmpegSource);
    }

    [Fact]
    public void Resolve_Item8_ToolsFolderWinsOverProgramFilesAndPath()
    {
        var (tools, preset, path) = MakeAllFour();

        FfmpegResolution resolution = FfmpegTool.Resolve(Env(), tools, preset, path);

        Assert.Equal(Path.Combine(tools, "ffmpeg.exe"), resolution.Ffmpeg);
        Assert.Equal("repo:tools\\ffmpeg", resolution.FfmpegSource);
        Assert.Equal(Path.Combine(tools, "ffprobe.exe"), resolution.Ffprobe);
        Assert.Equal("next-to-ffmpeg", resolution.FfprobeSource);
    }

    [Fact]
    public void Resolve_Item8_OnlyProgramFiles()
    {
        string preset = MakeFile("default", "ffmpeg.exe");

        FfmpegResolution resolution = FfmpegTool.Resolve(Env(), Path.Combine(_root, "no-tools"), Path.GetDirectoryName(preset), Path.Combine(_root, "empty"));

        Assert.Equal(preset, resolution.Ffmpeg);
        Assert.Equal("default-dir", resolution.FfmpegSource);
    }

    [Fact]
    public void Resolve_Item8_OnlyPath()
    {
        string onPath = MakeFile("path", "ffmpeg.exe");

        FfmpegResolution resolution = FfmpegTool.Resolve(Env(), Path.Combine(_root, "no-tools"), Path.Combine(_root, "no-default"), Path.GetDirectoryName(onPath));

        Assert.Equal(onPath, resolution.Ffmpeg);
        Assert.Equal("PATH", resolution.FfmpegSource);
    }

    [SkippableFact]
    public void ToolsDirectory_IsTheRepositoryToolsFfmpeg()
    {
        // 自己試験の置き換えが無い前提（ふだんの回）。
        Skip.If(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FfmpegTool.ToolsDirectoryEnvironmentVariable)),
            "TCS_FFMPEG_TOOLS_DIR is set");
        string? tools = FfmpegTool.ToolsDirectory;
        Assert.NotNull(tools);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(tools)!)!, "TimecodeSyncPlayer.slnx")));
        Assert.EndsWith(Path.Combine("tools", "ffmpeg"), tools);
    }

    // ---- 試験基盤の 8: 版の判定（6 未満なら始める前に止める） ----

    [Theory]
    [InlineData("ffmpeg version 4.2.3 Copyright (c) 2000-2020 the FFmpeg developers\nlibavcodec     58. 54.100 / 58. 54.100", true)]
    [InlineData("ffmpeg version n5.0 Copyright (c) 2000-2022 the FFmpeg developers\nlibavcodec     59. 18.100 / 59. 18.100", true)]
    [InlineData("ffmpeg version 5.0 Copyright (c) 2000-2022 the FFmpeg developers", true)]
    [InlineData("ffmpeg version 6.0-full_build-www.gyan.dev Copyright (c) 2000-2023 the FFmpeg developers", false)]
    [InlineData("ffmpeg version 8.0.1-full_build-www.gyan.dev Copyright (c) 2000-2025 the FFmpeg developers\r\nlibavcodec     62. 11.100 / 62. 11.100", false)]
    [InlineData("ffmpeg -version failed: The system cannot find the file specified.", true)]
    public void VersionStopReason_StopsBelowSix(string output, bool stops)
    {
        var resolution = new FfmpegResolution(@"C:\x\ffmpeg.exe", "default-dir", @"C:\x\ffprobe.exe", "next-to-ffmpeg");

        string? stop = FfmpegTool.VersionStopReason(resolution, output);

        Assert.Equal(stops, stop is not null);
        if (stop is not null)
        {
            Assert.Contains(@"C:\x\ffmpeg.exe", stop);
            Assert.Contains("default-dir", stop);
            Assert.Contains(output.Split('\n')[0].Trim(), stop);
            Assert.Contains("get-ffmpeg.ps1", stop);
        }
    }

    [Fact]
    public void VersionStopReason_NotFound_DoesNotStop() =>
        // 見つからない（名前で起動）ときは従来どおり起動の失敗で分かる（スキップの判定に任せる）。
        Assert.Null(FfmpegTool.VersionStopReason(new FfmpegResolution("ffmpeg", "name", "ffprobe", "name"), "ffmpeg -version failed: x"));

    [Fact]
    public void Resolve_FfprobeEnvironmentWinsOverTheFfmpegFolder()
    {
        string pinned = MakeFile("pinned", "ffmpeg.exe");
        MakeFile("pinned", "ffprobe.exe");
        string probe = MakeFile("probe", "ffprobe.exe");

        FfmpegResolution resolution = FfmpegTool.Resolve(Env(ffmpeg: pinned, ffprobe: probe), null, null, null);

        Assert.Equal(probe, resolution.Ffprobe);
        Assert.Equal("env:TCS_FFPROBE", resolution.FfprobeSource);
    }

    [Fact]
    public void Resolve_EnvironmentPointingNowhere_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            FfmpegTool.Resolve(Env(ffmpeg: Path.Combine(_root, "missing", "ffmpeg.exe")), null, null, null));

    [Fact]
    public void Resolve_NothingFound_FallsBackToTheNames()
    {
        FfmpegResolution resolution = FfmpegTool.Resolve(Env(), Path.Combine(_root, "no-tools"), Path.Combine(_root, "no-default"), Path.Combine(_root, "empty"));
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
