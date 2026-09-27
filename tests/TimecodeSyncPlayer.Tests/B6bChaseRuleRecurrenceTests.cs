using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.4 B6b: 着地窓（D37-b2/d/e/f）・先行量・T9 の ±0.20 窓を消す前に、それらが守っていた
/// 欠陥が chase モデルの規則（relocate の閾値 = max(tol, 学習したシークの所要)、着地待ちが
/// 連鎖を止める、relocate の直後の 1 サンプルは varispeed しない）でも起きないことを固定する。
/// **畳み込みの前後で緑**であることが条件（赤になったら欠陥が守れていない＝止めて報告）。
/// </summary>
public class B6bChaseRuleRecurrenceTests
{
    private static SyncPlaybackState State(double playbackSeconds) => new(
        SyncEnabled: true,
        HasCurrentTrack: true,
        IsSeeking: false,
        PlaybackSeconds: playbackSeconds,
        DurationSeconds: 20.0,
        VideoFps: 30.0,
        TimecodeFps: 30.0);

    private static TimecodeSyncService Service() =>
        new(new SyncDecisionEngine(), new TimecodeSyncSeekState());

    // ── D37-b: 重い素材で不足を速度補正に任せて追い付けない ──────────────

    [Fact]
    public void D37b_DeficitWithinLearnedSeekCost_UsesRateCatchUp_AndBeyondSeeks()
    {
        // v0.5.4 B6b（追補 3）: 閾値 = max(tol, r_max × c)。c = 1.0・r_max = 0.10・tol = 2 フレーム（30fps で
        // 約 0.067）に対し、0.09 は varispeed、0.15 は relocate（予測ロケートで 1 回で詰める）。
        var within = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2));
        within.UpdateSeekCostSeconds(1.0);
        SyncDecision rate = within.Decide(4.09, State(4.0));  // delta 0.09 <= 0.10

        rate.Action.Should().Be(SyncActionType.None);
        rate.RateCatchUpPreferred.Should().BeTrue();

        var beyond = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2));
        beyond.UpdateSeekCostSeconds(1.0);
        SyncDecision seek = beyond.Decide(4.15, State(4.0));  // delta 0.15 > 0.10

        seek.Action.Should().Be(SyncActionType.Seek);
    }

    // ── D37-b2: ギャップ明け・トラック切替の着地直後にシークで着地しない ──

    [Fact]
    public void D37b2_AfterALoadLanding_ADeficitBeyondTheThresholdRelocates()
    {
        TimecodeSyncService service = Service();
        service.SetSeekCostHintSeconds(1.0);
        service.BeginFileLoad(startPositionSeconds: 0.0, renderedFrameCount: 0);
        service.ObserveLandingState(
            new PlaybackPositionSample(0.0, PlaybackPositionBasis.Pipeline, 1, 0.0, 1, 1),
            toleranceSeconds: 0.24);
        service.IsLoadingFile.Should().BeFalse("前提: 着地の事象でロードが解除された");

        SyncDecision decision = service.EvaluateDecision(6.0, State(4.0));   // delta 2.0 > 閾値 1.0

        decision.Action.Should().Be(SyncActionType.Seek,
            "着地直後でも、閾値を超える不足は relocate する（着地窓の代わり）");
    }

    // ── D37-d: 前進しないシークの連鎖 ────────────────────────────────

    [Fact]
    public void D37d_WhileASeekIsPending_NoSecondSeekIsIssued()
    {
        TimecodeSyncService service = Service();
        service.ReportSeekSent(5.0);
        var state = State(1.0);
        int seeks = 0;
        service.SeekIssued += () => seeks++;

        for (int i = 0; i < 5; i++)
        {
            SyncDecision decision = service.EvaluateDecision(5.05, state);
            decision.Action.Should().Be(SyncActionType.None,
                "着地待ちの間は判定しない（連鎖は着地待ちが止める）");
        }

        seeks.Should().Be(0, "着地しない限り次のシークは出ない");
        service.SeekState.TargetSeconds.Should().Be(5.0, "近い要求で着地待ちの目標を置き換えない");
        service.IsWaitingForLanding.Should().BeTrue();
    }

    // ── D37-e: 先行量なしの着地で残差が残り続ける ─────────────────────

    [Fact]
    public void D37e_ResidualAtTheLearnedSeekCost_RelocatesToLtcPlusTheSeekCost()
    {
        // v0.5.4 B6b（追補 3、規則 3 の予測ロケート）: 先行量なしの着地後の残差（≒ c）は閾値
        // max(tol, r_max × c) を超えるので relocate する。目標は M(now) + c なので、着地したときに
        // M に追い付き、残差 c を作り直さない（鎖にならないことはシナリオ層の
        // B6bPredictiveLocateTests で固定する）。
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(ToleranceFrames: 2));
        engine.UpdateSeekCostSeconds(1.0);

        SyncDecision decision = engine.Decide(4.95, State(4.0) with { SeekTargetLookaheadSeconds = 1.0 });

        decision.Action.Should().Be(SyncActionType.Seek);
        decision.TargetSeconds.Should().BeApproximately(5.95, 1e-9, "目標 = M(now) + c");
    }

    // ── 16/23: relocate の直後の 1 サンプルは varispeed しない（着地窓・±0.20 の窓の代わり） ──

    [Fact]
    public void Rule3_JustLanded_IsConsumedByExactlyOneSample()
    {
        var seekState = new TimecodeSyncSeekState();
        DateTime now = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);
        seekState.BeginSeek(5.0, now);
        seekState.ConsumeJustLanded().Should().BeFalse("着地する前は立たない");

        seekState.ObserveLandingSample(
            new PlaybackPositionSample(5.0, PlaybackPositionBasis.Pipeline, 1, 5.0, 1, 1),
            0.24, now.AddMilliseconds(200));

        seekState.ConsumeJustLanded().Should().BeTrue("着地を観測した直後の 1 サンプル");
        seekState.ConsumeJustLanded().Should().BeFalse("2 サンプル目からは補正を評価する");
    }

    [Fact]
    public void Rule3_AfterALoadLanding_TheFirstSampleDoesNotVarispeed_AndTheNextDoes()
    {
        var harness = new TimecodeSyncPlayer.Tests.Integration.SyncScenarioHarness(enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.ManualPlay();
        harness.SupplyLtc(1.0);                                   // clip1 へ切替（読み込み）
        harness.AdvancePlayback(1.1, renderedFrames: 2);

        harness.SupplyLtc(1.2);                                   // 着地を観測したサンプル（残差 +100ms）

        harness.AppliedRates.Should().BeEmpty("relocate・読み込みの着地の直後の 1 サンプルは varispeed しない");

        harness.SupplyLtc(1.2);                                   // 次のサンプル

        harness.AppliedRates.Should().ContainSingle().Which.Should().BeApproximately(1.10, 1e-9);
    }
}
