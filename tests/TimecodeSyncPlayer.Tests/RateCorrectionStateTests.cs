using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.2 段 2d: <see cref="RateCorrectionState"/> の各メソッドが変える値を 1 件ずつ確かめる。
/// 倍率と戻し待ちは段階（Unity / Applied / RestorePending）なので、その遷移も確かめる。
/// </summary>
public class RateCorrectionStateTests
{
    [Fact]
    public void MarkRateApplied_SetsAppliedStage()
    {
        var state = new RateCorrectionState();

        state.MarkRateApplied(0.98);

        state.LastAppliedRate.Should().Be(0.98);
        state.RateNotUnity.Should().BeTrue();
        state.RateRestorePending.Should().BeFalse();
    }

    [Fact]
    public void MarkRateApplied_One_MovesToUnity()
    {
        var state = new RateCorrectionState();
        state.MarkRateApplied(0.98);

        state.MarkRateApplied(1.0);

        state.LastAppliedRate.Should().Be(1.0);
        state.RateNotUnity.Should().BeFalse();
        state.RateRestorePending.Should().BeFalse();
    }

    [Fact]
    public void MarkRateApplied_KeepsTheExactValue_NearUnity()
    {
        // 1.0 の近傍でも値を丸めない（SetRate の変化判定が丸めで変わらないようにする）。
        var state = new RateCorrectionState();

        state.MarkRateApplied(1.0002);

        state.LastAppliedRate.Should().Be(1.0002);
        state.RateNotUnity.Should().BeFalse("判定幅 0.0005 の内側");
    }

    [Fact]
    public void MarkRestorePending_MovesFromAppliedAndKeepsRate()
    {
        var state = new RateCorrectionState();
        state.MarkRateApplied(0.98);

        state.MarkRestorePending();

        state.RateRestorePending.Should().BeTrue();
        state.LastAppliedRate.Should().Be(0.98);
        state.RateNotUnity.Should().BeTrue();
    }

    [Fact]
    public void MarkRestored_ReturnsToUnity()
    {
        var state = new RateCorrectionState();
        state.MarkRateApplied(0.98);
        state.MarkRestorePending();

        state.MarkRestored();

        state.RateRestorePending.Should().BeFalse();
        state.LastAppliedRate.Should().Be(1.0);
        state.RateNotUnity.Should().BeFalse();
    }

    [Fact]
    public void MarkSmoothUnavailable_SetsFlag()
    {
        var state = new RateCorrectionState();

        state.MarkSmoothUnavailable();

        state.SmoothAvailable.Should().BeFalse();
    }

    [Fact]
    public void ResetSmoothAvailability_LowersFlag()
    {
        var state = new RateCorrectionState();
        state.MarkSmoothUnavailable();

        state.ResetSmoothAvailability();

        state.SmoothAvailable.Should().BeTrue();
    }

    [Fact]
    public void EnterPositionPause_ReportsOnlyTheTransition()
    {
        var state = new RateCorrectionState();

        state.EnterPositionPause().Should().BeTrue("false→true のときだけログを出す");
        state.CorrectionPausedForPosition.Should().BeTrue();
        state.EnterPositionPause().Should().BeFalse("既に止まっている");
    }

    [Fact]
    public void ExitPositionPause_ReportsOnlyTheTransition()
    {
        var state = new RateCorrectionState();

        state.ExitPositionPause().Should().BeFalse("止まっていない");
        state.EnterPositionPause();

        state.ExitPositionPause().Should().BeTrue("true→false のときだけログを出す");
        state.CorrectionPausedForPosition.Should().BeFalse();
        state.ExitPositionPause().Should().BeFalse("既に再開している");
    }

    [Fact]
    public void MarkRejectedLogged_SetsFlag()
    {
        var state = new RateCorrectionState();

        state.MarkRejectedLogged();

        state.RejectedLogged.Should().BeTrue();
    }

    [Fact]
    public void ClearRejectedLogged_LowersFlag()
    {
        var state = new RateCorrectionState();
        state.MarkRejectedLogged();

        state.ClearRejectedLogged();

        state.RejectedLogged.Should().BeFalse();
    }

    [Fact]
    public void ObserveResidual_CountsRejectedSamples()
    {
        var state = new RateCorrectionState();

        state.ObserveResidual(1.0, toleranceSeconds: 0.2, nowSeconds: 0.0, granularitySeconds: 0.04)
            .Rejected.Should().BeFalse("最初のサンプルは比較対象が無い");
        SeekDecisionGate.Result rejected =
            state.ObserveResidual(5.0, toleranceSeconds: 0.2, nowSeconds: 0.2, granularitySeconds: 0.04);

        rejected.Rejected.Should().BeTrue("0.2 秒で 4.0 秒の変化は物理的にありえない");
        state.RejectedSamples.Should().Be(1);
    }

    [Fact]
    public void ResetResidualGate_CutsTheSeriesButKeepsTheCount()
    {
        var state = new RateCorrectionState();
        state.ObserveResidual(1.0, toleranceSeconds: 0.2, nowSeconds: 0.0, granularitySeconds: 0.04);
        state.ObserveResidual(5.0, toleranceSeconds: 0.2, nowSeconds: 0.2, granularitySeconds: 0.04);

        state.ResetResidualGate();

        // 系列を切ったので、次のサンプルは前の採用値と比べられない（弾かれない）。
        state.ObserveResidual(50.0, toleranceSeconds: 0.2, nowSeconds: 0.3, granularitySeconds: 0.04)
            .Rejected.Should().BeFalse();
        state.RejectedSamples.Should().Be(1, "Reset は弾いた回数を消さない（SeekDecisionGate と同じ）");
    }
}
