using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D33: Single + 同期 ON で LTC が [MediaIn, MediaOut] を越えたら終端でホールドし、範囲へ
/// 戻ったら解除して追従を再開する。Jump 補正は範囲外へシークしない。
/// </summary>
public sealed class LtcSingleClipEndHoldTests
{
    private static (SyncScenarioHarness Harness, ManualTimeProvider Clock) Arrange()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true) { GapBehavior = GapBehavior.Black };
        h.AddTrack("A", 0, 200);
        h.ReloadProject();
        h.SetDurationSeconds(200);
        h.MediaInSeconds = 5.0;
        h.MediaOutSeconds = 25.0;
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        return (h, clock);
    }

    private static IReadOnlyList<double> SeekTargets(SyncScenarioHarness h) =>
        h.Operations.Where(o => o.Name == "seek").Select(o => o.Value ?? double.NaN).ToList();

    [Fact]
    public void OutOfRangeLtc_AtClipOut_HoldsWithoutSeekOrCorrection()
    {
        (SyncScenarioHarness h, _) = Arrange();
        h.CorrectionMode = SyncCorrectionMode.Jump;
        h.AdvancePlayback(25.0);

        h.SupplyLtc(40.0);

        h.Operations.Should().Contain(o => o.Name == "clip-end-hold");
        h.IsPaused.Should().BeTrue("MediaOut の最終フレームで静止する");
        SeekTargets(h).Should().NotContain(t => t > 25.5, "範囲外へシークしない（Jump 補正を含む）");
    }

    [Fact]
    public void OutOfRangeLtc_BeforeBoundary_SeeksToClipOut_AndJumpCorrectionStaysInside()
    {
        (SyncScenarioHarness h, _) = Arrange();
        h.CorrectionMode = SyncCorrectionMode.Jump;
        h.AdvancePlayback(10.0);

        h.SupplyLtc(40.0);

        // 粗い判定は D29 の clamp で MediaOut へ着地する。補正（Jump）は範囲外なので動かない。
        SeekTargets(h).Should().Contain(25.0);
        SeekTargets(h).Should().NotContain(t => t > 25.5);
    }

    [Fact]
    public void ClipOutHold_ReleasesAndFollowsWhenLtcReturnsInside()
    {
        (SyncScenarioHarness h, _) = Arrange();
        h.AdvancePlayback(25.0);
        h.SupplyLtc(40.0);
        h.Operations.Should().Contain(o => o.Name == "clip-end-hold");
        h.Operations.Clear();

        h.SupplyLtc(10.0);

        h.Operations.Should().Contain(o => o.Name == "clip-end-release");
        h.IsPaused.Should().BeFalse();
        SeekTargets(h).Should().Contain(10.0);
    }

    [Fact]
    public void HeldDuplicateLtc_AtClipOut_HoldsWithoutSeek()
    {
        // D33: LTC が保持（Duplicate）のまま境界に着地した場合でもホールドする
        // （通常の同期評価が走らないため、保持フレーム用の評価で止める）。
        (SyncScenarioHarness h, _) = Arrange();
        h.AdvancePlayback(25.0);

        h.SupplyHeldLtc(40.0);

        h.Operations.Should().Contain(o => o.Name == "clip-end-hold");
        h.IsPaused.Should().BeTrue("MediaOut の最終フレームで静止する");
        SeekTargets(h).Should().BeEmpty("保持フレームではシークしない");
    }
}
