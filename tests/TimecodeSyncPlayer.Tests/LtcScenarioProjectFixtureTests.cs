using System.IO;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// LTC シナリオ E2E の既定プロジェクト（Fixtures/ltc-scenario.tsp、色素材 A/B/C）を
/// 読み戻し、タイムライン配置（先頭オフセット 5 秒、各 20 秒 + ギャップ 5 秒）を固定する。
/// 素材は scripts/make-e2e-media.ps1 が生成する（無い環境では実在確認だけスキップ）。
/// </summary>
[Collection("Project serializer state")]
public sealed class LtcScenarioProjectFixtureTests
{
    [SkippableFact]
    public async Task LtcScenarioProject_FixtureHasColorTracksAtFixedOffsets()
    {
        string fixture = Path.Combine(FindRepositoryRoot(), "tests", "TimecodeSyncPlayer.Tests",
            "Fixtures", "ltc-scenario.tsp");
        Skip.If(!File.Exists(fixture), "Fixtures/ltc-scenario.tsp が無い");

        ProjectData? project = await ProjectSerializer.LoadAsync(fixture);

        project.Should().NotBeNull();
        project!.SyncMode.Should().Be(SyncMode.Single);
        project.GapBehavior.Should().Be(GapBehavior.Black);
        project.Tracks.Should().HaveCount(3);

        string[] symbols = ["A", "B", "C"];
        double[] offsets = [5, 30, 55];
        for (int index = 0; index < 3; index++)
        {
            TrackData track = project.Tracks[index];
            track.Name.Should().Be(symbols[index]);
            track.TimelineOffset.Should().Be(TimeSpan.FromSeconds(offsets[index]));
            track.MediaIn.Should().Be(TimeSpan.Zero);
            track.MediaOut.Should().Be(TimeSpan.FromSeconds(20));
            track.MediaDuration.Should().Be(TimeSpan.FromSeconds(20));
            track.FrameRate.Should().Be(30);
            track.IsEnabled.Should().BeTrue();
        }
    }

    [SkippableFact]
    public async Task LtcScenarioProject_CopiedBesideMediaResolvesEveryTrack()
    {
        string mediaDir = Path.Combine(FindRepositoryRoot(), "artifacts", "media");
        string[] mediaNames = ["ltc_a.mp4", "ltc_b.mp4", "ltc_c.mp4"];
        Skip.If(mediaNames.Any(name => !File.Exists(Path.Combine(mediaDir, name))),
            "色素材が無い（scripts/make-e2e-media.ps1）");

        string fixture = Path.Combine(FindRepositoryRoot(), "tests", "TimecodeSyncPlayer.Tests",
            "Fixtures", "ltc-scenario.tsp");
        Skip.If(!File.Exists(fixture), "Fixtures/ltc-scenario.tsp が無い");

        string projectPath = Path.Combine(mediaDir, "ltc-scenario.tsp");
        File.Copy(fixture, projectPath, overwrite: true);

        ProjectData? project = await ProjectSerializer.LoadAsync(projectPath);

        project.Should().NotBeNull();
        foreach (TrackData track in project!.Tracks)
            File.Exists(track.FilePath).Should().BeTrue($"素材が解決できる: {track.Name}");
    }

    /// <summary>
    /// 実素材のランナーと同じ形: 一時ディレクトリに素材ダミー（media\ 配下）と .tsp を置き、
    /// 素材を .tsp のディレクトリからの相対パス（media\&lt;ファイル名&gt;）で書く。
    /// ProjectSerializer がプロジェクトディレクトリ内の相対パスを解決できることを固定する。
    /// （ランナーは ReportDir\media に素材へのハードリンクを張り、そのパスを -MediaDir に渡す）
    /// </summary>
    [SkippableFact]
    public async Task LtcScenarioProject_MediaSubdirectoryRelativePaths_Resolve()
    {
        string fixture = Path.Combine(FindRepositoryRoot(), "tests", "TimecodeSyncPlayer.Tests",
            "Fixtures", "ltc-scenario.tsp");
        Skip.If(!File.Exists(fixture), "Fixtures/ltc-scenario.tsp が無い");

        string projectDir = Path.Combine(Path.GetTempPath(), "tcs-ltc-project-rel",
            Guid.NewGuid().ToString("N"));
        string mediaDir = Path.Combine(projectDir, "media");
        Directory.CreateDirectory(mediaDir);
        string[] mediaNames = ["m1.mp4", "m2.mp4", "m3.mp4"];
        foreach (string name in mediaNames)
            await File.WriteAllBytesAsync(Path.Combine(mediaDir, name), new byte[16]);

        string projectPath = Path.Combine(projectDir, "ltc-scenario.tsp");
        try
        {
            JsonNode node = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!;
            JsonArray tracks = node["tracks"]!.AsArray();
            for (int index = 0; index < tracks.Count; index++)
            {
                tracks[index]!["name"] = $"M{index + 1}";
                tracks[index]!["filePath"] = Path.Combine("media", mediaNames[index]);
            }

            await File.WriteAllTextAsync(projectPath, node.ToJsonString());

            ProjectData? project = await ProjectSerializer.LoadAsync(projectPath);

            project.Should().NotBeNull();
            project!.Tracks.Should().HaveCount(mediaNames.Length);
            for (int index = 0; index < mediaNames.Length; index++)
            {
                string expected = Path.GetFullPath(Path.Combine(mediaDir, mediaNames[index]));
                project.Tracks[index].FilePath.Should().Be(expected,
                    "プロジェクトディレクトリ内の相対パス（media\\<ファイル名>）を解決できる");
                File.Exists(project.Tracks[index].FilePath).Should().BeTrue(
                    $"相対パスの素材が実在する: M{index + 1}");
            }
        }
        finally
        {
            try { Directory.Delete(projectDir, recursive: true); } catch { /* 一時ファイルの後始末 */ }
        }
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TimecodeSyncPlayer.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("テストは worktree 内のビルド出力から実行される");
        return dir!.FullName;
    }
}
