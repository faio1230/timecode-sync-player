using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D37-a: 粗い同期シークのゲート（瞬間値で Seek を出さない）の固定。
/// </summary>
public sealed class SeekDecisionGateTests
{
    private const double Tolerance = 0.240;      // 6 フレーム @25fps
    private const double Granularity = 0.040;    // 1/25
    private const double Step = 0.050;           // LTC 40ms + ゆらぎ

    // 検証機の実測系列（ms）。+118.4 と +253 は 50ms で動けない跳ね。
    private static readonly double[] OscillatingMs = [11.3, 39.6, -1.0, 118.4, -11.4, 253.0];

    [Fact]
    public void Observe_OscillatingResidual_NeverSeeksAndCountsRejected()
    {
        var gate = new SeekDecisionGate();
        double now = 0.0;
        bool shouldSeek = false;
        foreach (double deltaMs in OscillatingMs)
        {
            now += Step;
            SeekDecisionGate.Result result = gate.Observe(deltaMs / 1000.0, Tolerance, now, Granularity);
            shouldSeek |= result.ShouldSeek;
        }

        shouldSeek.Should().BeFalse("振動の中央値は許容の内側に留まる");
        gate.RejectedSamples.Should().Be(2, "+118.4ms と +253ms は物理的にありえない変化");
        gate.Samples.Should().Be(0, "弾いた後は系列を切って測り直す（乱れの前後を混ぜない）");
        gate.ConsecutiveExceeded.Should().Be(0);
    }

    [Fact]
    public void Observe_RejectionClearsTheSeries_AndTheNextSamplesStartOver()
    {
        var gate = new SeekDecisionGate();
        double now = 0.0;
        gate.Observe(0.0, Tolerance, now += Step, Granularity);
        gate.Observe(0.3, Tolerance, now += Step, Granularity).Rejected.Should().BeTrue(
            "50ms で 0.3 秒は動けない");
        gate.Samples.Should().Be(0);

        gate.Observe(0.3, Tolerance, now += Step, Granularity).ShouldSeek.Should().BeFalse("1 サンプル目");
        gate.Observe(0.3, Tolerance, now += Step, Granularity).ShouldSeek.Should().BeFalse("2 サンプル目");
        gate.Observe(0.3, Tolerance, now += Step, Granularity).ShouldSeek.Should().BeTrue("3 サンプル目");
    }

    [Fact]
    public void Observe_RejectionAlsoResetsTheConsecutiveCount()
    {
        // 0.4.6: Codex のレビューで再現されたもの。異常値を弾いて系列を切っても
        // 連続超過の回数だけが残り、切った直後の 1 サンプル目が「連続 5 回超過」になっていた。
        // 標本の間隔が広い（200ms）と窓（250ms）に 3 標本そろわず中央値の判定は働かないので、
        // 連続回数だけが積み上がる。
        const double wideStep = 0.200;
        var gate = new SeekDecisionGate();
        double now = 0.0;
        for (int i = 0; i < 4; i++)
            gate.Observe(0.25, Tolerance, now += wideStep, Granularity).ShouldSeek.Should().BeFalse(
                $"{i + 1} 回目の超過（まだ 5 回に届かない）");
        gate.ConsecutiveExceeded.Should().Be(4);

        gate.Observe(0.70, Tolerance, now += wideStep, Granularity).Rejected.Should().BeTrue(
            "200ms で 0.45 秒は動けない");
        gate.ConsecutiveExceeded.Should().Be(0, "系列を切ったなら連続回数も切る");

        gate.Observe(0.25, Tolerance, now += wideStep, Granularity).ShouldSeek.Should().BeFalse(
            "切った直後の 1 サンプル目。以前はここで連続 5 回と数えてシークしていた");
    }

    [Fact]
    public void Observe_SustainedOffset_SeeksAfterTheWindowFills()
    {
        var gate = new SeekDecisionGate();
        double now = 0.0;
        gate.Observe(0.3, Tolerance, now += Step, Granularity).ShouldSeek.Should().BeFalse();
        gate.Observe(0.3, Tolerance, now += Step, Granularity).ShouldSeek.Should().BeFalse();
        gate.Observe(0.3, Tolerance, now += Step, Granularity).ShouldSeek.Should().BeTrue(
            "3 サンプル（150ms）そろえば中央値が許容を超える");
    }

    [Fact]
    public void Observe_SparseSamples_SeekAfterConsecutiveLimit()
    {
        // 窓（250ms）より疎な 400ms 間隔。中央値が使えないので連続条件だけで拾う。
        var gate = new SeekDecisionGate();
        double now = 0.0;
        for (int i = 1; i <= 4; i++)
            gate.Observe(0.3, Tolerance, now += 0.4, Granularity).ShouldSeek.Should().BeFalse($"連続 {i} 回");
        gate.Observe(0.3, Tolerance, now += 0.4, Granularity).ShouldSeek.Should().BeTrue("連続 5 回");
    }

    [Fact]
    public void Observe_ImpossibleJump_IsRejectedAndNotAddedToTheWindow()
    {
        var gate = new SeekDecisionGate();
        double now = 0.0;
        gate.Observe(0.05, Tolerance, now += Step, Granularity);
        SeekDecisionGate.Result result = gate.Observe(0.55, Tolerance, now += Step, Granularity);

        result.Rejected.Should().BeTrue("50ms で 0.5 秒は動けない");
        result.RejectedTotal.Should().Be(1);
        gate.Samples.Should().Be(0, "弾いた後は系列を切る");
    }

    [Fact]
    public void Observe_ChangeWithinTheRateLimit_IsAccepted()
    {
        // 50ms × 1.2 + 40ms = 100ms まで許す。99ms の変化は実在しうる（着地直後の追い込み）。
        var gate = new SeekDecisionGate();
        double now = 0.0;
        gate.Observe(0.0, Tolerance, now += Step, Granularity);
        SeekDecisionGate.Result result = gate.Observe(0.099, Tolerance, now += Step, Granularity);

        result.Rejected.Should().BeFalse();
        gate.RejectedSamples.Should().Be(0);
    }

    [Fact]
    public void Observe_ExactlyAtTolerance_IsNotCountedAsExceeded()
    {
        var gate = new SeekDecisionGate();
        double now = 0.0;
        for (int i = 0; i < 6; i++)
            gate.Observe(Tolerance, Tolerance, now += Step, Granularity).ShouldSeek.Should().BeFalse();
        gate.ConsecutiveExceeded.Should().Be(0);
    }

    [Fact]
    public void Reset_ClearsTheSeriesButKeepsTheRejectedCount()
    {
        var gate = new SeekDecisionGate();
        double now = 0.0;
        gate.Observe(0.05, Tolerance, now += Step, Granularity);
        gate.Observe(0.55, Tolerance, now += Step, Granularity); // 弾かれる
        gate.Observe(0.10, Tolerance, now += Step, Granularity);
        gate.Observe(0.15, Tolerance, now += Step, Granularity);

        gate.Reset();

        gate.Samples.Should().Be(0);
        gate.ConsecutiveExceeded.Should().Be(0);
        gate.RejectedSamples.Should().Be(1);
        gate.Observe(0.3, Tolerance, now += Step, Granularity).ShouldSeek.Should().BeFalse(
            "リセット後は窓が埋まるまで出さない");
    }
}
