using System.IO;
using System.Text.Json.Nodes;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// 段階 4.1: キャンバス・クリップ配置の保存形式と旧ファイル互換。
/// ProjectSerializer は ProjectPath 静的状態を共有するため、ProjectSerializerTests と同じ
/// 一時ディレクトリを使い、同じ collection で直列実行する。
/// </summary>
[Collection("Project serializer state")]
public class ProjectSerializerCanvasTests : IDisposable
{
    private const string FilePrefix = "canvas-";

    private readonly string _tempDir;

    public ProjectSerializerCanvasTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TimecodeSyncPlayer.Tests", "ProjectSerializer");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempDir)) return;
        foreach (string file in Directory.GetFiles(_tempDir, $"{FilePrefix}*"))
        {
            try { File.Delete(file); } catch { }
        }
    }

    private string GetTempPath(string fileName) => Path.Combine(_tempDir, FilePrefix + fileName);

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    private static async Task<ProjectData?> LoadWithWarningsAsync(string path, List<LogEvent> events)
    {
        var sink = new ListSink();
        ILogger previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            return await ProjectSerializer.LoadAsync(path);
        }
        finally
        {
            Log.Logger = previous;
            lock (sink.Events) events.AddRange(sink.Events);
        }
    }

    private static void RemoveCanvasAndFit(string path)
    {
        JsonNode node = JsonNode.Parse(File.ReadAllText(path))!;
        node.AsObject().Remove("canvas");
        if (node["tracks"] is JsonArray tracks)
        {
            foreach (JsonNode? track in tracks)
                track?.AsObject().Remove("fit");
        }
        File.WriteAllText(path, node.ToJsonString(), System.Text.Encoding.UTF8);
    }

    [Fact]
    public async Task LoadAsync_OldProjectWithoutCanvasAndFit_ReturnsNulls()
    {
        string media = GetTempPath("old.mp4");
        await File.WriteAllTextAsync(media, "");
        var playlist = new PlaylistState();
        playlist.AddFiles([media]);
        string path = GetTempPath("old.tsp");

        await ProjectSerializer.SaveAsync(path, playlist, SyncMode.Continue, GapBehavior.Black,
            new CanvasData { Width = 3840, Height = 2160, DefaultFit = "fit-width" });
        RemoveCanvasAndFit(path);

        ProjectData? project = await ProjectSerializer.LoadAsync(path);

        project.Should().NotBeNull();
        project!.Canvas.Should().BeNull();
        project.Tracks.Should().ContainSingle();
        project.Tracks[0].Fit.Should().BeNull();
        project.Version.Should().Be(1);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip_PreservesCanvasAndTrackFit()
    {
        string media = GetTempPath("roundtrip.mp4");
        await File.WriteAllTextAsync(media, "");
        var playlist = new PlaylistState();
        playlist.AddFiles([media]);
        playlist.Tracks[0] = playlist.Tracks[0] with { Fit = "fit-width" };
        string path = GetTempPath("roundtrip.tsp");

        await ProjectSerializer.SaveAsync(path, playlist, SyncMode.Continue, GapBehavior.Black,
            new CanvasData { Width = 3840, Height = 2160, DefaultFit = "fit-width" });
        ProjectData loaded = (await ProjectSerializer.LoadAsync(path))!;

        loaded.Canvas.Should().NotBeNull();
        loaded.Canvas!.Width.Should().Be(3840);
        loaded.Canvas.Height.Should().Be(2160);
        loaded.Canvas.DefaultFit.Should().Be("fit-width");
        loaded.Tracks[0].Fit.Should().Be("fit-width");

        // 読み込んだ正規化値をそのまま書き戻しても保持される（往復の正規化保存）。
        var restored = new PlaylistState();
        ProjectSerializer.ApplyToPlaylist(loaded, restored);
        string path2 = GetTempPath("roundtrip2.tsp");
        await ProjectSerializer.SaveAsync(path2, restored, SyncMode.Continue, GapBehavior.Black, loaded.Canvas);
        ProjectData loaded2 = (await ProjectSerializer.LoadAsync(path2))!;

        loaded2.Canvas.Should().Be(loaded.Canvas);
        loaded2.Tracks[0].Fit.Should().Be("fit-width");
    }

    [Fact]
    public async Task SaveAsync_WithoutCanvas_OmitsCanvasAndFitProperties()
    {
        string media = GetTempPath("omitted.mp4");
        await File.WriteAllTextAsync(media, "");
        var playlist = new PlaylistState();
        playlist.AddFiles([media]);
        string path = GetTempPath("omitted.tsp");

        await ProjectSerializer.SaveAsync(path, playlist, SyncMode.Single, GapBehavior.Black);
        string json = await File.ReadAllTextAsync(path);

        json.Should().NotContain("\"canvas\"");
        json.Should().NotContain("\"fit\"");
    }

    [Fact]
    public async Task LoadAsync_UnknownFitIds_WarnsAndNormalizes()
    {
        string media = GetTempPath("unknown.mp4");
        await File.WriteAllTextAsync(media, "");
        var playlist = new PlaylistState();
        playlist.AddFiles([media]);
        string path = GetTempPath("unknown.tsp");
        await ProjectSerializer.SaveAsync(path, playlist, SyncMode.Single, GapBehavior.Black,
            new CanvasData { Width = 1920, Height = 1080, DefaultFit = "fit-height" });

        JsonNode node = JsonNode.Parse(File.ReadAllText(path))!;
        node["canvas"]!["defaultFit"] = "fit-diagonal";
        node["tracks"]!.AsArray()[0]!["fit"] = "stretch";
        File.WriteAllText(path, node.ToJsonString(), System.Text.Encoding.UTF8);

        var events = new List<LogEvent>();
        ProjectData? project = await LoadWithWarningsAsync(path, events);

        project!.Canvas!.DefaultFit.Should().Be("fit-height");
        project.Tracks[0].Fit.Should().BeNull();
        events.Should().Contain(e => e.MessageTemplate.Text.Contains("未知の配置設定"));
        events.Where(e => e.MessageTemplate.Text.Contains("未知の配置設定"))
            .Should().HaveCount(2, "defaultFit と track fit の両方で警告する");
    }

    [Theory]
    [InlineData(15, 1080)]
    [InlineData(1920, 16385)]
    [InlineData(0, 0)]
    public async Task LoadAsync_OutOfRangeCanvasDimensions_WarnsAndReplacesWithDefault(int width, int height)
    {
        string media = GetTempPath($"range-{width}x{height}.mp4");
        await File.WriteAllTextAsync(media, "");
        var playlist = new PlaylistState();
        playlist.AddFiles([media]);
        string path = GetTempPath($"range-{width}x{height}.tsp");
        await ProjectSerializer.SaveAsync(path, playlist, SyncMode.Single, GapBehavior.Black,
            new CanvasData { Width = 1920, Height = 1080, DefaultFit = "fit-width" });

        JsonNode node = JsonNode.Parse(File.ReadAllText(path))!;
        node["canvas"]!["width"] = width;
        node["canvas"]!["height"] = height;
        File.WriteAllText(path, node.ToJsonString(), System.Text.Encoding.UTF8);

        var events = new List<LogEvent>();
        ProjectData? project = await LoadWithWarningsAsync(path, events);

        project!.Canvas!.Width.Should().Be(1920);
        project.Canvas.Height.Should().Be(1080);
        project.Canvas.DefaultFit.Should().Be("fit-width", "正常な defaultFit は寸法置換で失わない");
        events.Should().Contain(e => e.MessageTemplate.Text.Contains("範囲外"));
    }
}
