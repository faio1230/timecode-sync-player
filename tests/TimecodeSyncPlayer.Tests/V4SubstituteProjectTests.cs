using System.IO;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// V4: 現場プロジェクトが未提供の間に使う代替プロジェクトを生成・検証する。
/// 素材と同じ artifacts/media に置き、相対パスで参照する
/// （ProjectSerializer はプロジェクトディレクトリ外のパスを拒否するため、
/// 別ディレクトリの fixture から素材を参照できない）。
/// artifacts/media が無い環境ではスキップする。
/// 構成（docs/prompts/2026-09-17-V4-gap-project-substitute.md）:
///   A  = 0:00:10 から test_1080p60.mp4（MediaIn 0、SyncOffset 0）          [10,40)
///   ギャップ1 = 0:00:40〜0:00:50（Black）
///   B  = 0:00:50 から test_720p25.mkv（MediaIn 2 秒、SyncOffset +0.5 秒）  [50,68)
///   ギャップ2 = B 終端〜+8 秒
///   D  = 無効トラック（C より前の行優先で IsEnabled=false）                [76,96) 無効
///   C  = 0:00:76 から test_1080p60.mp4                                     [76,106)
/// 素材ファイルはこのテストでは変更しない（.tsp のみ生成）。
/// </summary>
[Collection("Project serializer state")]
public sealed class V4SubstituteProjectTests
{
    [SkippableFact]
    public async Task SubstituteProject_IsGeneratedAndLoads()
    {
        string root = FindRepositoryRoot();
        string mediaDir = Path.Combine(root, "artifacts", "media");
        string clip1080 = Path.Combine(mediaDir, "test_1080p60.mp4");
        string clip720Mkv = Path.Combine(mediaDir, "test_720p25.mkv");
        string clip720Avi = Path.Combine(mediaDir, "test_720p25.avi");
        Skip.If(!File.Exists(clip1080) || !File.Exists(clip720Mkv) || !File.Exists(clip720Avi),
            "artifacts/media の E2E 素材が必要（scripts/make-e2e-media.ps1）");

        string projectPath = Path.Combine(mediaDir, "v4-substitute.tsp");
        var playlist = new PlaylistState();
        playlist.Tracks.Add(new PlaylistTrack(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), clip1080, "Substitute_A",
            TimeSpan.Zero, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
            TimeSpan.Zero, 60, true));
        playlist.Tracks.Add(new PlaylistTrack(
            Guid.Parse("22222222-2222-2222-2222-222222222222"), clip720Mkv, "Substitute_B",
            TimeSpan.FromSeconds(2), null, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(0.5), 25, true));
        playlist.Tracks.Add(new PlaylistTrack(
            Guid.Parse("33333333-3333-3333-3333-333333333333"), clip720Avi, "Substitute_D_disabled",
            TimeSpan.Zero, null, TimeSpan.FromSeconds(76), TimeSpan.FromSeconds(20),
            TimeSpan.Zero, 25, false));
        playlist.Tracks.Add(new PlaylistTrack(
            Guid.Parse("44444444-4444-4444-4444-444444444444"), clip1080, "Substitute_C",
            TimeSpan.Zero, null, TimeSpan.FromSeconds(76), TimeSpan.FromSeconds(30),
            TimeSpan.Zero, 60, true));

        await ProjectSerializer.SaveAsync(projectPath, playlist, SyncMode.Continue, GapBehavior.Black,
            new CanvasData { Width = 1920, Height = 1080, DefaultFit = "fit-height" });

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
