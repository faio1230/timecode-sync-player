using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// T5: 同期補正モード（Smooth = 比例制御のレート微調整 / Jump = フラッシュシーク）。
/// Smooth: rate = 1 + clamp(e / T, -0.10, +0.10)、T=1.0s、デッドバンド 20ms。
/// T9: 着地直後の 1.0 秒だけ上限 ±0.20。
/// Smooth はシークを発行しない。Jump は連続 3 回で諦める。
/// </summary>
public class SyncCorrectionControllerTests
{
    private static readonly DateTime T0 = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private static SyncCorrectionDecision Evaluate(
        SyncCorrectionController controller,
        double residualSeconds,
        SyncCorrectionMode mode = SyncCorrectionMode.Smooth,
        bool smoothAvailable = true,
        double targetSeconds = 10.0,
        double secondsAfterStart = 0.0)
        => controller.Evaluate(residualSeconds, targetSeconds, mode, smoothAvailable, T0.AddSeconds(secondsAfterStart));

    [Theory]
    [InlineData(0.150, 1.10)]
    [InlineData(0.050, 1.05)]
    [InlineData(0.025, 1.025)]
    [InlineData(-0.050, 0.95)]
    [InlineData(-0.150, 0.90)]
    public void Smooth_UsesProportionalLaw_WithSymmetricSign(double residual, double expectedRate)
    {
        var controller = new SyncCorrectionController();

        SyncCorrectionDecision decision = Evaluate(controller, residual);

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

        Evaluate(controller, 0.010).Action.Should().Be(SyncCorrectionActionType.None);
        Evaluate(controller, -0.010).Action.Should().Be(SyncCorrectionActionType.None);
        Evaluate(controller, 0.0).Action.Should().Be(SyncCorrectionActionType.None);
    }

    [Fact]
    public void Smooth_ActiveCorrection_ReturnsRateToOneInsideHysteresisBand_ThenIdles()
    {
        var controller = new SyncCorrectionController();
        Evaluate(controller, 0.150).Rate.Should().BeApproximately(1.10, 1e-9);

        // デッドバンド(20ms)内へ入るとヒステリシスで rate 1.0 に戻る。
        SyncCorrectionDecision settled = Evaluate(controller, 0.005, secondsAfterStart: 0.1);
        Evaluate(controller, 0.005, secondsAfterStart: 0.2).Action.Should().Be(SyncCorrectionActionType.None);

        settled.Action.Should().Be(SyncCorrectionActionType.SetRate);
        settled.Rate.Should().Be(1.0);
        settled.Reason.Should().Be("smooth-settled");
    }

    [Fact]
    public void Smooth_Hysteresis_KeepsCorrectingBetweenReturnBandAndDeadband()
    {
        var controller = new SyncCorrectionController();
        Evaluate(controller, 0.150).Rate.Should().BeApproximately(1.10, 1e-9);

        // 10〜20ms は「補正中なら継続、未補正なら何もしない」。
        SyncCorrectionDecision active = Evaluate(controller, 0.015, secondsAfterStart: 0.1);
        var idle = new SyncCorrectionController();
        SyncCorrectionDecision fresh = Evaluate(idle, 0.015, secondsAfterStart: 0.1);

        active.Action.Should().Be(SyncCorrectionActionType.SetRate);
        active.Rate.Should().BeApproximately(1.015, 1e-9);
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
    public void Jump_StopsAfterConsecutiveSeekLimit_AndResetsWhenResidualSettles()
    {
        var controller = new SyncCorrectionController();

        for (int i = 0; i < 3; i++)
            Evaluate(controller, 0.120, SyncCorrectionMode.Jump, secondsAfterStart: i * 0.1)
                .Action.Should().Be(SyncCorrectionActionType.Seek);

        Evaluate(controller, 0.120, SyncCorrectionMode.Jump, secondsAfterStart: 0.4)
            .Action.Should().Be(SyncCorrectionActionType.None);
        Evaluate(controller, 0.120, SyncCorrectionMode.Jump, secondsAfterStart: 0.5)
            .Action.Should().Be(SyncCorrectionActionType.None);

        Evaluate(controller, 0.010, SyncCorrectionMode.Jump, secondsAfterStart: 0.6)
            .Action.Should().Be(SyncCorrectionActionType.None);
        Evaluate(controller, 0.120, SyncCorrectionMode.Jump, secondsAfterStart: 0.7)
            .Action.Should().Be(SyncCorrectionActionType.Seek);
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

    // ── T9: 着地直後 1.0 秒の速度上限 ±0.20 ────────────────────────────

    [Theory]
    [InlineData(0.200, 1.20)]
    [InlineData(-0.200, 0.80)]
    public void Smooth_InsideLandingWindow_UsesTwentyPercentLimit(double residual, double expectedRate)
    {
        var controller = new SyncCorrectionController();
        controller.NotifyLanding(T0);

        SyncCorrectionDecision decision = Evaluate(controller, residual, secondsAfterStart: 0.5);

        decision.Action.Should().Be(SyncCorrectionActionType.SetRate);
        decision.Rate.Should().BeApproximately(expectedRate, 1e-9);
    }

    [Theory]
    [InlineData(0.200, 1.10)]
    [InlineData(-0.200, 0.90)]
    public void Smooth_AfterLandingWindow_UsesTenPercentLimit(double residual, double expectedRate)
    {
        var controller = new SyncCorrectionController();
        controller.NotifyLanding(T0);

        SyncCorrectionDecision decision = Evaluate(controller, residual, secondsAfterStart: 1.5);

        decision.Rate.Should().BeApproximately(expectedRate, 1e-9);
    }

    [Fact]
    public void Smooth_InsideLandingWindow_KeepsProportionalLaw()
    {
        var controller = new SyncCorrectionController();
        controller.NotifyLanding(T0);

        Evaluate(controller, 0.050, secondsAfterStart: 0.5).Rate.Should().BeApproximately(1.05, 1e-9);
    }

    [Fact]
    public void Smooth_LandingWindowEnd_RoundsAppliedRateIntoTenPercentRange()
    {
        var controller = new SyncCorrectionController();
        controller.NotifyLanding(T0);
        Evaluate(controller, 0.500, secondsAfterStart: 0.5).Rate.Should().BeApproximately(1.20, 1e-9);

        SyncCorrectionDecision after = Evaluate(controller, 0.500, secondsAfterStart: 1.5);

        after.Rate.Should().BeApproximately(1.10, 1e-9);
    }

    [Fact]
    public void Smooth_NewLandingInsideWindow_RestartsOneSecondWindow()
    {
        var controller = new SyncCorrectionController();
        controller.NotifyLanding(T0);
        Evaluate(controller, 0.200, secondsAfterStart: 0.9).Rate.Should().BeApproximately(1.20, 1e-9);

        controller.NotifyLanding(T0.AddSeconds(0.9));

        Evaluate(controller, 0.200, secondsAfterStart: 1.5).Rate.Should().BeApproximately(1.20, 1e-9);
        Evaluate(controller, 0.200, secondsAfterStart: 2.0).Rate.Should().BeApproximately(1.10, 1e-9);
    }

    [Fact]
    public void Reset_ClearsLandingWindow()
    {
        var controller = new SyncCorrectionController();
        controller.NotifyLanding(T0);
        Evaluate(controller, 0.200, secondsAfterStart: 0.5).Rate.Should().BeApproximately(1.20, 1e-9);

        controller.Reset();

        Evaluate(controller, 0.200, secondsAfterStart: 0.5).Rate.Should().BeApproximately(1.10, 1e-9);
    }

    [Fact]
    public void Jump_IgnoresLandingWindow()
    {
        var withLanding = new SyncCorrectionController();
        withLanding.NotifyLanding(T0);
        var withoutLanding = new SyncCorrectionController();

        foreach (double residual in new[] { 0.010, 0.120, -0.120 })
        {
            SyncCorrectionDecision expected = withoutLanding.Evaluate(
                residual, 10.0, SyncCorrectionMode.Jump, true, T0.AddSeconds(0.5));
            SyncCorrectionDecision actual = withLanding.Evaluate(
                residual, 10.0, SyncCorrectionMode.Jump, true, T0.AddSeconds(0.5));

            actual.Should().Be(expected);
        }
    }
}
