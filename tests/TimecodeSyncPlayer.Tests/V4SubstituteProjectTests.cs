using System.IO;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// V4: 現場プロジェクトが未提供の間に使う代替プロジェクトを検証し、実行用に配置する。
/// コミット済みの Fixtures/v4-substitute.tsp（素材名だけの相対パス）を
/// artifacts/media へコピーしてから読み戻す。ProjectSerializer はプロジェクト
/// ディレクトリ外のパスを拒否するため、.tsp は素材と同じディレクトリに置く必要がある。
/// 構成（docs/prompts/2026-09-17-V4-gap-project-substitute.md）:
///   A  = 0:00:10 から test_1080p60.mp4（MediaIn 0、SyncOffset 0）          [10,40)
///   ギャップ1 = 0:00:40〜0:00:50（Black）
///   B  = 0:00:50 から test_720p25.mkv（MediaIn 2 秒、SyncOffset +0.5 秒）  [50,68)
///   ギャップ2 = B 終端〜+8 秒
///   D  = 無効トラック（C より前の行優先で IsEnabled=false）                [76,96) 無効
///   C  = 0:00:76 から test_1080p60.mp4                                     [76,106)
/// artifacts/media が無い環境ではスキップする。
/// </summary>
[Collection("Project serializer state")]
public sealed class V4SubstituteProjectTests
{
    [SkippableFact]
    public async Task SubstituteProject_IsProvisionedAndLoads()
    {
        string root = FindRepositoryRoot();
        string mediaDir = Path.Combine(root, "artifacts", "media");
        string clip1080 = Path.Combine(mediaDir, "test_1080p60.mp4");
        string clip720Mkv = Path.Combine(mediaDir, "test_720p25.mkv");
        string clip720Avi = Path.Combine(mediaDir, "test_720p25.avi");
        Skip.If(!File.Exists(clip1080) || !File.Exists(clip720Mkv) || !File.Exists(clip720Avi),
            "artifacts/media の E2E 素材が必要（scripts/make-e2e-media.ps1）");

        string template = Path.Combine(root, "tests", "TimecodeSyncPlayer.Tests", "Fixtures",
            "v4-substitute.tsp");
        Skip.If(!File.Exists(template), "Fixtures/v4-substitute.tsp が無い");

        string projectPath = Path.Combine(mediaDir, "v4-substitute.tsp");
        File.Copy(template, projectPath, overwrite: true);

        ProjectData? loaded = await ProjectSerializer.LoadAsync(projectPath);
        loaded.Should().NotBeNull();
        loaded!.Version.Should().Be(1);
        loaded.SyncMode.Should().Be(SyncMode.Continue);
        loaded.GapBehavior.Should().Be(GapBehavior.Black);
        loaded.Canvas.Should().NotBeNull();
        loaded.Canvas!.Width.Should().Be(1920);
        loaded.Tracks.Should().HaveCount(4);
        loaded.Tracks.Where(t => t.IsEnabled).Should().HaveCount(3);
        loaded.Tracks.Single(t => t.Name == "Substitute_D_disabled").IsEnabled.Should().BeFalse();

        foreach (TrackData track in loaded.Tracks.Where(t => t.IsEnabled))
            File.Exists(track.FilePath).Should().BeTrue($"素材が解決できる: {track.FilePath}");

        TrackData a = loaded.Tracks.Single(t => t.Name == "Substitute_A");
        a.FilePath.Should().Be(Path.GetFullPath(clip1080));
        a.TimelineOffset.Should().Be(TimeSpan.FromSeconds(10));
        a.MediaIn.Should().Be(TimeSpan.Zero);
        a.SyncOffset.Should().Be(TimeSpan.Zero);
        a.FrameRate.Should().Be(60);

        TrackData b = loaded.Tracks.Single(t => t.Name == "Substitute_B");
        b.FilePath.Should().Be(Path.GetFullPath(clip720Mkv));
        b.MediaIn.Should().Be(TimeSpan.FromSeconds(2));
        b.SyncOffset.Should().Be(TimeSpan.FromSeconds(0.5));
        b.FrameRate.Should().Be(25);

        TrackData c = loaded.Tracks.Single(t => t.Name == "Substitute_C");
        c.FilePath.Should().Be(Path.GetFullPath(clip1080));
        c.TimelineOffset.Should().Be(TimeSpan.FromSeconds(76));
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
