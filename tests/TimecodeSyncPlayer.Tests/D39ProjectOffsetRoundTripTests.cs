using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.4 D39 K1 の保存・読み込みの層（設計 §4-2）。編集したオフセットが
/// 後から届いた長さの更新で壊れていないことを、保存したファイルと読み直しで固定する。
/// </summary>
[Collection("Project serializer state")]
public class D39ProjectOffsetRoundTripTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SavedProject_KeepsEditedOffset_AfterLateDurationUpdate()
    {
        string tempDir = TestTempPaths.Combine("D39OffsetRace");
        Directory.CreateDirectory(tempDir);
        string fileA = Path.Combine(tempDir, "A.mp4");
        string fileB = Path.Combine(tempDir, "B.mp4");
        await File.WriteAllTextAsync(fileA, "");
        await File.WriteAllTextAsync(fileB, "");

        try
        {
            var state = new PlaylistState();
            state.AddFiles([fileA, fileB]);
            var clock = new ScenarioClock(BaseUtc, monotonicMilliseconds: 0);
            Guid editedId = state.Tracks[0].Id;
            Guid secondId = state.Tracks[1].Id;
            clock.Schedule(600, () => state.UpdateMediaDuration(editedId, TimeSpan.FromSeconds(20)));
            clock.Schedule(700, () => state.UpdateMediaDuration(secondId, TimeSpan.FromSeconds(30)));

            PlaylistTimelineOffsetEditor.Apply(state, editedId, "00:00:10:00", autoOffset: true, fallbackFps: 25)
                .Status.Should().Be(PlaylistTimelineOffsetEditStatus.Applied);
            clock.Advance(TimeSpan.FromMilliseconds(700));

            string projectPath = Path.Combine(tempDir, "d39-offset.tsp");
            await ProjectSerializer.SaveAsync(projectPath, state, SyncMode.Continue, GapBehavior.Freeze);

            ProjectData? project = await ProjectSerializer.LoadAsync(projectPath);
            var restored = new PlaylistState();
            ProjectSerializer.ApplyToPlaylist(project!, restored);

            restored.Tracks[0].TimelineOffset.Should().Be(TimeSpan.FromSeconds(10),
                "保存したプロジェクトのオフセットは読み直しても 10 秒のまま");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
