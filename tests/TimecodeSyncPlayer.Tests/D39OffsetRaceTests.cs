using FluentAssertions;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.4 D39 K1 の決定的な再現（設計: docs/design/v0.5.4-scenario-layer.md §4-2）。
/// ファイル 2 本追加 → 2 行目を選ぶ → 1 行目のオフセットを 00:00:10:00 に編集 →
/// 長さの更新が後から届く、の順序を ScenarioClock の予約で固定する。
/// </summary>
public class D39OffsetRaceTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    private static (PlaylistState State, ScenarioClock Clock) Arrange()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:/media/A.mp4", "C:/media/B.mp4"]);
        var clock = new ScenarioClock(BaseUtc, monotonicMilliseconds: 0);
        return (state, clock);
    }

    private static (PlaylistState State, ScenarioClock Clock) ArrangeWithLateDurations()
    {
        (PlaylistState state, ScenarioClock clock) = Arrange();
        Guid editedId = state.Tracks[0].Id;
        Guid secondId = state.Tracks[1].Id;
        clock.Schedule(600, () => state.UpdateMediaDuration(editedId, TimeSpan.FromSeconds(20)));
        clock.Schedule(700, () => state.UpdateMediaDuration(secondId, TimeSpan.FromSeconds(30)));

        state.Select(1);    // 2 行目を選ぶ
        PlaylistTimelineOffsetEditor.Apply(state, editedId, "00:00:10:00", autoOffset: true, fallbackFps: 25)
            .Status.Should().Be(PlaylistTimelineOffsetEditStatus.Applied);

        clock.Advance(TimeSpan.FromMilliseconds(700));   // 長さの更新が後から届く
        return (state, clock);
    }

    [Fact]
    public void EditedOffset_SurvivesLateDurationUpdate()
    {
        (PlaylistState state, _) = ArrangeWithLateDurations();

        state.Tracks[0].TimelineOffset.Should().Be(TimeSpan.FromSeconds(10),
            "利用者が編集したオフセットは、後から届いた長さの更新で上書きされない");
    }

    [Fact]
    public void AutomaticLayout_ContinuesAfterTheEditedOffset()
    {
        (PlaylistState state, _) = ArrangeWithLateDurations();

        state.Tracks[1].TimelineOffset.Should().Be(TimeSpan.FromSeconds(30),
            "編集した行の実効終端（10 秒 + 20 秒）から自動配置を続ける");
    }

    [Fact]
    public void SelectedTrack_CanBeFoundByIdAfterLateDurationReplacement()
    {
        (PlaylistState state, ScenarioClock clock) = Arrange();
        PlaylistTrack selected = state.Tracks[1];
        Guid selectedId = selected.Id;
        state.Select(1);
        clock.Schedule(700, () => state.UpdateMediaDuration(selectedId, TimeSpan.FromSeconds(30)));

        clock.Advance(TimeSpan.FromMilliseconds(700));

        state.Tracks[1].Should().NotBeSameAs(selected,
            "長さの更新は行を差し替える（WPF の ListBox はここで選択を外す）");
        PlaylistSelectionRestore.IndexAfterReplacement(state, selected).Should().Be(1,
            "差し替えで外れた選択は同じ Id の行へ戻せる（上へ/下への操作を続けられる）");
        state.Tracks[1].Id.Should().Be(selectedId, "選択は Id で追える（インスタンスは変わる）");
    }

    [Fact]
    public void SelectionRestore_DoesNotReviveRemovedOrDeselectedTrack()
    {
        (PlaylistState state, _) = Arrange();
        PlaylistTrack second = state.Tracks[1];

        state.RemoveAt(1);
        PlaylistSelectionRestore.IndexAfterReplacement(state, second).Should().BeNull("削除で消えた行は戻さない");

        PlaylistTrack unchanged = state.Tracks[0];
        PlaylistSelectionRestore.IndexAfterReplacement(state, unchanged).Should().BeNull(
            "同じインスタンスが残っている（利用者が空クリックで外した）なら戻さない");
    }
}
