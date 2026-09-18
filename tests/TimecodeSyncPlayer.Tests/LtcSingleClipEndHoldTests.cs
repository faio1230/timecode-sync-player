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

    private static void Tick(SyncScenarioHarness h, ManualTimeProvider clock, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            h.Tick100Milliseconds();
        }
    }

    private static LtcFrameProcessingResult Processed(double seconds, TimecodeFrameDiagnosticStatus status) =>
        new("scenario", $"{seconds:F3} s", seconds, 25, "fps: 25",
            new TimecodeFrameDiagnosticResult(status, 0, 0),
            ShouldApplySync: status is TimecodeFrameDiagnosticStatus.Normal or TimecodeFrameDiagnosticStatus.Initial,
            ShouldLogFps: false);

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

    [Fact]
    public void BoundaryHold_DoesNotIssueExplicitLandingToTheEdge()
    {
        // D35-b (1): 境界ホールド中は端への明示着地を発行しない。端の 2 フレーム以内で
        // 1 フレーム超の残差（24.94 対 25）でも、保留シークを増やさない。
        (SyncScenarioHarness h, ManualTimeProvider clock) = Arrange();
        h.SignalLossMode = LtcSignalLossMode.Stop;
        h.AdvancePlayback(24.94);

        h.SupplyLtc(24.9);   // 有効フレーム（進行の時計を開始）
        h.Controller.ReceiveProcessedFrame(Processed(40.0, TimecodeFrameDiagnosticStatus.Reverse), 10_000);
        Tick(h, clock, 3);   // 範囲外の非適用フレームのみ → 信号断として一時停止

        h.IsPaused.Should().BeTrue();
        h.Operations.Clear();

        h.SupplyHeldLtc(40.0);   // 40 保持 → 境界ホールド成立

        h.Operations.Should().Contain(o => o.Name == "clip-end-hold");
        SeekTargets(h).Should().BeEmpty("境界ホールド中は端への明示着地を発行しない");
    }

    [Fact]
    public void BoundaryHoldRelease_ClearsPendingSeek_AndLandsOnTheNewLtc()
    {
        // D35-b (2): S-3 の系列。40 保持で端へ clamp した pending が残ったまま 10 保持で
        // 解除しても、解除時に pending と保持着地のラッチを解除し、10 へ 1 回着地する。
        (SyncScenarioHarness h, ManualTimeProvider clock) = Arrange();
        h.SignalLossMode = LtcSignalLossMode.Stop;
        h.AdvancePlayback(10.0);

        h.SupplyLtc(40.0);       // 端 25 へ clamp シーク（pending=25）
        SeekTargets(h).Should().Contain(25.0);

        h.SupplyHeldLtc(40.0);   // 40 保持 → 境界ホールド
        h.Operations.Should().Contain(o => o.Name == "clip-end-hold");
        h.Operations.Clear();

        clock.Advance(TimeSpan.FromSeconds(1));   // デバウンス窓を明ける
        h.SupplyHeldLtc(10.0);   // 10 保持 → 解除 → pending に抑止されず 10 へ
        Tick(h, clock, 3);       // D37-a: 粗い判定のゲート（3 サンプル）が開くまで保留を再送する

        h.Operations.Should().Contain(o => o.Name == "clip-end-release");
        SeekTargets(h).Should().Equal(new[] { 10.0 }, "解除後は新しい範囲内 LTC へ 1 回だけ着地する");

        h.SupplyHeldLtc(10.0);
        SeekTargets(h).Should().Equal(new[] { 10.0 }, "同じ値の連続では繰り返さない");
    }
}
