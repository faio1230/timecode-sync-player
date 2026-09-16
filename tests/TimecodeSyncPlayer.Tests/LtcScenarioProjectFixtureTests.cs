using System.IO;
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

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TimecodeSyncPlayer.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("テストは worktree 内のビルド出力から実行される");
        return dir!.FullName;
    }
}
