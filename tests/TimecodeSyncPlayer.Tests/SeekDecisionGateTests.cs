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
        // 0.4.8: 外れ値を弾いても、弾く前の系列（-11.4 は -1.0 とつながる）は続ける。
        gate.Samples.Should().BeGreaterThan(0);
        gate.ConsecutiveExceeded.Should().Be(0);
    }

    [Fact]
    public void Observe_ConfirmedJump_StartsANewSeriesAtTheSameTimingAsBefore()
    {
        // 0.4.8: 弾いた値が次のサンプルでも続けば本物の跳びとして、そこから新しい系列を始める。
        // シークまでのサンプル数は以前（弾いた次から測り直す）と同じ。
        var gate = new SeekDecisionGate();
        double now = 0.0;
        gate.Observe(0.0, Tolerance, now += Step, Granularity);
        gate.Observe(0.3, Tolerance, now += Step, Granularity).Rejected.Should().BeTrue(
            "50ms で 0.3 秒は動けない");
        gate.Samples.Should().Be(1, "弾いたサンプルは窓に入れず、前の系列は残す");

        gate.Observe(0.3, Tolerance, now += Step, Granularity).ShouldSeek.Should().BeFalse("1 サンプル目");
        gate.Observe(0.3, Tolerance, now += Step, Granularity).ShouldSeek.Should().BeFalse("2 サンプル目");
        gate.Observe(0.3, Tolerance, now += Step, Granularity).ShouldSeek.Should().BeTrue("3 サンプル目");
    }

    [Fact]
    public void Observe_AlternatingOutliers_AreNeverAdoptedAsTheSeries()
    {
        // 0.4.8: UIA 50ms 監査の失敗で実測した形。復号が追いつかずパイプライン位置が 2 系列を
        // 行き来すると、残差が 1 サンプルおきに約 +150ms 跳ねる。以前は弾くたびに系列を消したので、
        // 弾いた次の外れ値が採用され、中央値が真値と外れ値の間を往復した。
        var gate = new SeekDecisionGate();
        double now = 0.0;
        var medians = new List<double>();
        for (int i = 0; i < 40; i++)
        {
            double truth = -0.003 - 0.001 * i;           // ゆっくり動く真の残差
            double observed = i % 2 == 0 ? truth : truth + 0.15;
            SeekDecisionGate.Result result = gate.Observe(observed, Tolerance, now += Step, Granularity);
            if (!result.Rejected)
                medians.Add(result.MedianSeconds);
            result.ShouldSeek.Should().BeFalse();
        }

        gate.RejectedSamples.Should().Be(20, "外れ値はすべて弾く");
        medians.Should().OnlyContain(m => m < 0.0, "採用される系列は真値だけ");
    }

    [Fact]
    public void Observe_OutlierThenReturnToTheSeries_KeepsTheSeries()
    {
        var gate = new SeekDecisionGate();
        double now = 0.0;
        gate.Observe(0.30, Tolerance, now += Step, Granularity);
        gate.Observe(0.30, Tolerance, now += Step, Granularity);
        gate.Observe(0.90, Tolerance, now += Step, Granularity).Rejected.Should().BeTrue();
        SeekDecisionGate.Result back = gate.Observe(0.30, Tolerance, now += Step, Granularity);

        back.Rejected.Should().BeFalse("弾く前の系列とつながる");
        back.Samples.Should().Be(3, "外れ値 1 つで系列を捨てない");
        back.ShouldSeek.Should().BeTrue("続いている不足は外れ値に邪魔されずにシークへ進む");
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
        gate.Samples.Should().Be(1, "弾いたサンプルは窓に入れない（前の系列は残す）");
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
