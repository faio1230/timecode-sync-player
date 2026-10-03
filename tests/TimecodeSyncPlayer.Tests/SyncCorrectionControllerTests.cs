using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// T5: 同期補正モード（Smooth = 比例制御のレート微調整 / Jump = フラッシュシーク）。
/// Smooth: rate = 1 + clamp(e / T, -0.10, +0.10)、T=1.0s、不感帯は 1 映像フレーム
/// （B4b。fps から呼び出し側が決める）、戻りバンド 2ms（T2 段 3）。
/// T9: 着地直後の 1.0 秒だけ上限 ±0.20。
/// T8: Jump はしきい値 80ms を超えたらシーク、連続 3 回で止まり、内側に 1 秒留まると戻る。
/// Smooth はシークを発行しない。
/// </summary>
public class SyncCorrectionControllerTests
{
    private static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>B4b: 不感帯の既定（1 映像フレーム。25fps で 40ms）。</summary>
    private const double OneFrameAt25Fps = 1.0 / 25.0;

    private static SyncCorrectionDecision Evaluate(
        SyncCorrectionController controller,
        double residualSeconds,
        SyncCorrectionMode mode = SyncCorrectionMode.Smooth,
        bool smoothAvailable = true,
        double targetSeconds = 10.0,
        double secondsAfterStart = 0.0,
        double deadbandSeconds = OneFrameAt25Fps)
        => controller.Evaluate(
            residualSeconds, targetSeconds, mode, smoothAvailable, T0.AddSeconds(secondsAfterStart),
            deadbandSeconds);

    [Theory]
    [InlineData(0.150, 1.10)]
    [InlineData(0.050, 1.05)]
    [InlineData(0.025, 1.025)]
    [InlineData(-0.050, 0.95)]
    [InlineData(-0.150, 0.90)]
    public void Smooth_UsesProportionalLaw_WithSymmetricSign(double residual, double expectedRate)
    {
        var controller = new SyncCorrectionController();

        // B4b の不感帯（1 映像フレーム）と独立に比例則だけを見る（20ms のフレームとして渡す）。
        SyncCorrectionDecision decision = Evaluate(controller, residual, deadbandSeconds: 0.020);

        decision.Action.Should().Be(SyncCorrectionActionType.SetRate);
        decision.Rate.Should().BeApproximately(expectedRate, 1e-9);
    }

    [Theory]
    [InlineData(1.0, 1.10)]
    [InlineData(-1.0, 0.90)]
    public void Smooth_RateDeltaIsClampedToTenPercent(double residual, double expectedRate)
    {
        var controller = new SyncCorrectionController();

        Evaluate(controller, residual).Rate.Should().BeApproximately(expectedRate, 1e-12);
    }

    [Fact]
    public void Smooth_ResidualInsideDeadband_DoesNothing()
    {
        var controller = new SyncCorrectionController();

        Evaluate(controller, 0.004).Action.Should().Be(SyncCorrectionActionType.None);
        Evaluate(controller, -0.004).Action.Should().Be(SyncCorrectionActionType.None);
        Evaluate(controller, 0.0).Action.Should().Be(SyncCorrectionActionType.None);
    }

    [Fact]
    public void Smooth_ActiveCorrection_ReturnsRateToOneInsideHysteresisBand_ThenIdles()
    {
        var controller = new SyncCorrectionController();
        Evaluate(controller, 0.150).Rate.Should().BeApproximately(1.10, 1e-9);

        // 戻りバンド(2ms)内へ入るとヒステリシスで rate 1.0 に戻る。
        SyncCorrectionDecision settled = Evaluate(controller, 0.001, secondsAfterStart: 0.1);
        Evaluate(controller, 0.001, secondsAfterStart: 0.2).Action.Should().Be(SyncCorrectionActionType.None);

        settled.Action.Should().Be(SyncCorrectionActionType.SetRate);
        settled.Rate.Should().Be(1.0);
        settled.Reason.Should().Be("smooth-settled");
    }

    [Fact]
    public void Smooth_Hysteresis_KeepsCorrectingBetweenReturnBandAndDeadband()
    {
        var controller = new SyncCorrectionController();
        Evaluate(controller, 0.150).Rate.Should().BeApproximately(1.10, 1e-9);

        // 戻りバンド（2ms）と不感帯（1 映像フレーム）の間は「補正中なら継続、未補正なら何もしない」。
        SyncCorrectionDecision active = Evaluate(controller, 0.004, secondsAfterStart: 0.1);
        var idle = new SyncCorrectionController();
        SyncCorrectionDecision fresh = Evaluate(idle, 0.004, secondsAfterStart: 0.1);

        active.Action.Should().Be(SyncCorrectionActionType.SetRate);
        active.Rate.Should().BeApproximately(1.004, 1e-9);
        fresh.Action.Should().Be(SyncCorrectionActionType.None);
    }

    [Fact]
    public void Smooth_NeverRequestsSeek_ForAnyResidual()
    {
        var controller = new SyncCorrectionController();
        double t = 0;
        foreach (double residual in new[] { 0.001, -0.02, 0.05, -0.2, 0.5, -1.0 })
        {
            Evaluate(controller, residual, secondsAfterStart: t).Action
                .Should().NotBe(SyncCorrectionActionType.Seek);
            t += 0.1;
        }
    }

    [Fact]
    public void Jump_ResidualOutsideDeadband_RequestsSeekToTarget()
    {
        var controller = new SyncCorrectionController();

        SyncCorrectionDecision decision = Evaluate(
            controller, residualSeconds: 0.120, mode: SyncCorrectionMode.Jump, targetSeconds: 42.5);

        decision.Action.Should().Be(SyncCorrectionActionType.Seek);
        decision.TargetSeconds.Should().Be(42.5);
    }

    [Fact]
    public void Jump_AfterConsecutiveSeeks_KeepsSeeking()
    {
        // B6-24: 旧は連続 3 回で止めていた（jump-limit）。いまはシークを止めず、計数と警告だけ。
        var controller = new SyncCorrectionController();

        for (int i = 0; i <= 5; i++)
            Evaluate(controller, 0.120, SyncCorrectionMode.Jump, secondsAfterStart: i * 0.1)
                .Action.Should().Be(SyncCorrectionActionType.Seek);

        Evaluate(controller, 0.010, SyncCorrectionMode.Jump, secondsAfterStart: 0.6);
        Evaluate(controller, 0.010, SyncCorrectionMode.Jump, secondsAfterStart: 1.7);
        Evaluate(controller, 0.120, SyncCorrectionMode.Jump, secondsAfterStart: 1.8)
            .Action.Should().Be(SyncCorrectionActionType.Seek, "1 秒セトルで数え直してもシークは続く");
    }

    // ── T8: Jump のしきい値 80ms と、1 秒セトルで連続回数を戻す ─────────

    [Fact]
    public void Jump_ResidualJitterInsideThreshold_NeverSeeks()
    {
        var controller = new SyncCorrectionController();
        double[] jitter = { 0.035, -0.030, 0.025, -0.038 };

        for (int i = 0; i < jitter.Length; i++)
        {
            Evaluate(controller, jitter[i], SyncCorrectionMode.Jump, secondsAfterStart: i * 0.05)
                .Action.Should().Be(SyncCorrectionActionType.None);
        }
    }

    [Fact]
    public void Jump_ResidualBeyondThreshold_SeeksOnce()
    {
        var controller = new SyncCorrectionController();

        SyncCorrectionDecision decision = Evaluate(
            controller, 0.100, SyncCorrectionMode.Jump, targetSeconds: 5.0);

        decision.Action.Should().Be(SyncCorrectionActionType.Seek);
        decision.TargetSeconds.Should().Be(5.0);
    }

    [Fact]
    public void Jump_BriefDipInsideThreshold_KeepsSeeking()
    {
        // 一時的にしきい値の内側へ入っても、シークは止まらない（計数だけが連続のまま）。
        var controller = new SyncCorrectionController();
        Evaluate(controller, 0.100, SyncCorrectionMode.Jump, secondsAfterStart: 0.0)
            .Action.Should().Be(SyncCorrectionActionType.Seek);
        Evaluate(controller, 0.100, SyncCorrectionMode.Jump, secondsAfterStart: 0.1)
            .Action.Should().Be(SyncCorrectionActionType.Seek);
        Evaluate(controller, 0.010, SyncCorrectionMode.Jump, secondsAfterStart: 0.2)
            .Action.Should().Be(SyncCorrectionActionType.None);

        Evaluate(controller, 0.100, SyncCorrectionMode.Jump, secondsAfterStart: 0.3)
            .Action.Should().Be(SyncCorrectionActionType.Seek, "一瞬の内側で止めない");
        Evaluate(controller, 0.100, SyncCorrectionMode.Jump, secondsAfterStart: 0.4)
            .Action.Should().Be(SyncCorrectionActionType.Seek, "3 回を過ぎてもシークは続く（B6-24）");
    }

    [Fact]
    public void Jump_Reset_ClearsTheChainCount()
    {
        var controller = new SyncCorrectionController();
        for (int i = 0; i <= 3; i++)
            Evaluate(controller, 0.100, SyncCorrectionMode.Jump, secondsAfterStart: i * 0.1)
                .Action.Should().Be(SyncCorrectionActionType.Seek);

        controller.Reset();

        Evaluate(controller, 0.100, SyncCorrectionMode.Jump, secondsAfterStart: 0.4)
            .Action.Should().Be(SyncCorrectionActionType.Seek, "リセット後もシークは続く（B6-24）");
    }

    [Fact]
    public void ZeroResidual_DoesNothing_InBothModes()
    {
        var controller = new SyncCorrectionController();

        Evaluate(controller, 0.0).Action.Should().Be(SyncCorrectionActionType.None);
        Evaluate(controller, 0.0, SyncCorrectionMode.Jump, secondsAfterStart: 0.1)
            .Action.Should().Be(SyncCorrectionActionType.None);
    }

    [Fact]
    public void Smooth_Unavailable_ReturnsNone_WithoutSeek_AndExposesStatus()
    {
        var controller = new SyncCorrectionController();

        SyncCorrectionDecision decision = Evaluate(controller, 0.120, smoothAvailable: false);

        decision.Action.Should().Be(SyncCorrectionActionType.None);
        decision.Reason.Should().Be("smooth-unavailable");
        controller.SmoothUnavailable.Should().BeTrue();
        controller.SmoothDisabled.Should().BeFalse();
    }

    [Fact]
    public void IneffectiveDetections_CountsEachFire_AndSurvivesReset()
    {
        // v0.6.4 段 3（設計書 3-3）: 「効いていない」の検出の発火を起動からの累計で数える（Sync hold summary の
        // smoothIneffective）。数えるだけで、検出の挙動は変えない。
        var controller = new SyncCorrectionController();
        controller.IneffectiveDetections.Should().Be(0);

        for (int i = 0; i <= 4; i++)
            Evaluate(controller, 0.120, secondsAfterStart: i * 0.5);
        controller.SmoothDisabled.Should().BeTrue();
        controller.IneffectiveDetections.Should().Be(1);

        Evaluate(controller, 0.120, secondsAfterStart: 2.5);   // 止めた後は発火しない
        controller.IneffectiveDetections.Should().Be(1);

        controller.Reset();
        controller.IneffectiveDetections.Should().Be(1, "Reset は補正の状態だけを消し、累計は残す");
        for (int i = 0; i <= 4; i++)
            Evaluate(controller, 0.120, secondsAfterStart: 10.0 + i * 0.5);
        controller.IneffectiveDetections.Should().Be(2);
    }

    [Fact]
    public void Smooth_IneffectiveWithinWindow_DisablesAndFallsBackToRateOne()
    {
        var controller = new SyncCorrectionController();

        Evaluate(controller, 0.120, secondsAfterStart: 0.0);
        Evaluate(controller, 0.120, secondsAfterStart: 0.5);
        Evaluate(controller, 0.120, secondsAfterStart: 1.0);
        Evaluate(controller, 0.120, secondsAfterStart: 1.5);
        SyncCorrectionDecision giveUp = Evaluate(controller, 0.120, secondsAfterStart: 2.0);
        SyncCorrectionDecision after = Evaluate(controller, 0.120, secondsAfterStart: 2.1);

        giveUp.Action.Should().Be(SyncCorrectionActionType.SetRate);
        giveUp.Rate.Should().Be(1.0);
        giveUp.Reason.Should().Be("smooth-ineffective");
        after.Action.Should().Be(SyncCorrectionActionType.None);
        after.Reason.Should().Be("smooth-disabled");
        controller.SmoothDisabled.Should().BeTrue();
    }

    [Fact]
    public void Smooth_ImprovingResidual_IsNotDisabled()
    {
        var controller = new SyncCorrectionController();

        Evaluate(controller, +0.160, secondsAfterStart: 0.0);
        Evaluate(controller, +0.120, secondsAfterStart: 0.5);
        Evaluate(controller, +0.080, secondsAfterStart: 1.0);
        Evaluate(controller, +0.050, secondsAfterStart: 1.5);
        SyncCorrectionDecision decision = Evaluate(controller, +0.050, secondsAfterStart: 2.0);

        controller.SmoothDisabled.Should().BeFalse();
        decision.Action.Should().Be(SyncCorrectionActionType.SetRate);
        decision.Rate.Should().BeApproximately(1.05, 1e-9);
    }

    // ── T2 段 3/B4b: 不感帯は 1 映像フレーム・戻り 2ms、「効かない」は 30ms 以上でだけ ──

    [Fact]
    public void Smooth_SmallResidual_KeepsCorrectingAndIsNotDisabledAfterTwoSeconds()
    {
        var controller = new SyncCorrectionController();

        // 60fps の 1 フレーム（16.7ms）を不感帯にすると、25ms は不感帯の外で補正が始まる。
        // 窓の開始の残差 25ms は 30ms 未満なので「効かない」と判定されない。
        for (int i = 0; i <= 20; i++)
        {
            SyncCorrectionDecision decision = Evaluate(
                controller, 0.025, secondsAfterStart: i * 0.1, deadbandSeconds: 1.0 / 60.0);
            decision.Action.Should().Be(SyncCorrectionActionType.SetRate);
            decision.Rate.Should().BeApproximately(1.025, 1e-9);
        }

        controller.SmoothDisabled.Should().BeFalse("窓の開始の残差が 30ms 未満では「効かない」と判定しない");
    }

    [Fact]
    public void Smooth_FourMsIdles_AndOnePointFiveMsSettlesAtRateOne()
    {
        var controller = new SyncCorrectionController();

        Evaluate(controller, 0.004).Action.Should().Be(SyncCorrectionActionType.None);

        Evaluate(controller, 0.100).Rate.Should().BeApproximately(1.10, 1e-9);
        SyncCorrectionDecision settled = Evaluate(controller, 0.0015, secondsAfterStart: 0.1);
        settled.Action.Should().Be(SyncCorrectionActionType.SetRate);
        settled.Rate.Should().Be(1.0);
        settled.Reason.Should().Be("smooth-settled");
    }

    [Fact]
    public void Smooth_LargeResidual_WithoutImprovement_IsStillDisabled()
    {
        var controller = new SyncCorrectionController();

        for (int i = 0; i <= 20; i++)
            Evaluate(controller, 0.100, secondsAfterStart: i * 0.1);

        controller.SmoothDisabled.Should().BeTrue("窓の開始の残差が 30ms 以上なら従来どおり判定する");
    }

    [Fact]
    public void Smooth_ResidualGrowsFromSmallToLarge_IsDisabledAfterTwoSecondsWithoutImprovement()
    {
        var controller = new SyncCorrectionController();

        // 45ms（不感帯 40ms の外）で補正が始まる（窓の開始は 45ms）。
        Evaluate(controller, 0.045, secondsAfterStart: 0.0);
        // 200ms へ悪化。開始値から 10ms 以上なので窓を取り直し、ゲートが開く。
        Evaluate(controller, 0.200, secondsAfterStart: 0.1);
        Evaluate(controller, 0.200, secondsAfterStart: 1.1);

        SyncCorrectionDecision giveUp = Evaluate(controller, 0.200, secondsAfterStart: 2.2);

        controller.SmoothDisabled.Should().BeTrue();
        giveUp.Rate.Should().Be(1.0);
        giveUp.Reason.Should().Be("smooth-ineffective");
    }

    [Fact]
    public void Smooth_LargeResidual_ImprovingWithinTwoSeconds_IsNotDisabled()
    {
        var controller = new SyncCorrectionController();

        Evaluate(controller, 0.200, secondsAfterStart: 0.0);
        // 2 秒以内に 150ms へ改善（10ms 以上の改善で窓を取り直す）。
        SyncCorrectionDecision improved = Evaluate(controller, 0.150, secondsAfterStart: 0.9);
        Evaluate(controller, 0.150, secondsAfterStart: 1.5).Action
            .Should().Be(SyncCorrectionActionType.SetRate);

        controller.SmoothDisabled.Should().BeFalse();
        improved.Rate.Should().BeApproximately(1.10, 1e-9, "150ms は定常の ±10% でクランプされる");
    }

    [Fact]
    public void Reset_ClearsSmoothState_SoANewTrackCanTryAgain()
    {
        var controller = new SyncCorrectionController();
        Evaluate(controller, 0.120, secondsAfterStart: 0.0);
        Evaluate(controller, 0.120, secondsAfterStart: 2.0);
        controller.SmoothDisabled.Should().BeTrue();

        controller.Reset();

        controller.SmoothDisabled.Should().BeFalse();
        Evaluate(controller, 0.120, secondsAfterStart: 3.0)
            .Action.Should().Be(SyncCorrectionActionType.SetRate);
    }


    // ── 0.4.5-A フェーズ 1: shadow プレビュー（状態を変えない） ─────────

    [Theory]
    [InlineData(0.001, 1.0, "smooth-idle")]
    [InlineData(0.004, 1.0, "smooth-deadband")]
    [InlineData(0.050, 1.05, "smooth")]
    [InlineData(0.500, 1.10, "smooth")]
    [InlineData(double.NaN, 1.0, "invalid")]
    public void PreviewSmoothRate_UsesTheSameBandsAndClamp(
        double residual, double expectedRate, string expectedReason)
    {
        (double rate, string reason) = SyncCorrectionController.PreviewSmoothRate(
            residual, OneFrameAt25Fps);

        rate.Should().BeApproximately(expectedRate, 1e-9);
        reason.Should().Be(expectedReason);
    }

    [Fact]
    public void PreviewSmoothRate_DoesNotTouchControllerState()
    {
        var controller = new SyncCorrectionController();

        _ = SyncCorrectionController.PreviewSmoothRate(
            0.500, deadbandSeconds: OneFrameAt25Fps);

        // 呼んだ後も新しいコントローラと同じ（ヒステリシスの状態が変わっていない）。
        SyncCorrectionDecision decision = controller.Evaluate(
            0.001, 1.0, SyncCorrectionMode.Smooth, true, T0, OneFrameAt25Fps);
        decision.Action.Should().Be(SyncCorrectionActionType.None);
        decision.Reason.Should().Be("smooth-idle");
    }


    // ── B4b: 不感帯は 1 映像フレーム（fps から決める。定数は増やさない） ──

    [Theory]
    [InlineData(60.0, 25.0, 1.0 / 60.0)]   // 映像 fps を優先する
    [InlineData(0.0, 25.0, 1.0 / 25.0)]    // 映像 fps が不明なら LTC の 1 フレーム
    [InlineData(0.0, 0.0, 0.04)]           // どちらも不明なら 25fps の 1 フレーム
    public void FrameDurationSeconds_UsesVideoFpsThenTimecodeFps(
        double videoFps, double timecodeFps, double expectedSeconds)
    {
        SyncCorrectionController.FrameDurationSeconds(videoFps, timecodeFps)
            .Should().BeApproximately(expectedSeconds, 1e-12);
    }

    [Fact]
    public void Smooth_ResidualUpToTheCallersDeadband_DoesNothing()
    {
        var controller = new SyncCorrectionController();

        Evaluate(controller, 0.040, deadbandSeconds: 0.040).Action
            .Should().Be(SyncCorrectionActionType.None, "ちょうど 1 フレームは不感帯の中");
        Evaluate(controller, 0.041, deadbandSeconds: 0.040).Action
            .Should().Be(SyncCorrectionActionType.SetRate);
    }
}
