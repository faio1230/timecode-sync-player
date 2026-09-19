using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// 0.4.7: 「デコードが追いついていない」表示。表示するだけで同期の制御は変えない。
/// 数値は検証機の実測（60fps・2 秒の窓で期待 120 枚、落ち込み時 74 枚）に合わせている。
/// </summary>
public sealed class DecodeHealthMonitorTests
{
    private const double Fps = 60.0;
    private const double Window = 2.0;
    private static readonly DateTime T0 = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>乱れの無い窓を積み上げて、判定できる状態まで進める。</summary>
    private static (DecodeHealthMonitor Monitor, double RateIntegral, DateTime Now) Settled()
    {
        var monitor = new DecodeHealthMonitor();
        double integral = 0;
        DateTime now = T0;
        for (int i = 0; i <= DecodeHealthMonitor.SettleWindows; i++)
        {
            integral += Window;
            now += TimeSpan.FromSeconds(Window);
            monitor.Observe(120, Window, Fps, 0, integral, 30, 600, false, now);
        }
        return (monitor, integral, now);
    }

    [Fact]
    public void AMeasuredDropIsReportedAsBehind()
    {
        var (monitor, integral, now) = Settled();

        DecodeWindowVerdict verdict = monitor.Observe(74, Window, Fps, 0, integral + Window, 40, 600, false,
            now + TimeSpan.FromSeconds(Window));

        verdict.Should().Be(DecodeWindowVerdict.Behind, "検証機の実測: 2 秒で 120 枚のはずが 74 枚（0.6 倍速）");
        monitor.StatusText(now + TimeSpan.FromSeconds(Window)).Should().Contain("74 / 120");
    }

    [Fact]
    public void AFullWindowIsOk()
    {
        var (monitor, integral, now) = Settled();

        monitor.Observe(119, Window, Fps, 0, integral + Window, 40, 600, false, now + TimeSpan.FromSeconds(Window))
            .Should().Be(DecodeWindowVerdict.Ok);
        monitor.StatusText(now).Should().BeEmpty();
    }

    [Fact]
    public void ASlowerCommandedRateLowersTheExpectation()
    {
        // 補正で 0.8 倍速を指示していれば、2 秒で 96 枚は正常。
        var (monitor, integral, now) = Settled();

        monitor.Observe(96, Window, Fps, 0, integral + Window * 0.8, 40, 600, false, now + TimeSpan.FromSeconds(Window))
            .Should().Be(DecodeWindowVerdict.Ok);
    }

    [Fact]
    public void AWindowAcrossASeekAndTheTwoAfterAreNotJudged()
    {
        // シークは着地と再開の遅れで数秒フレームが減る（実測で最大 2.8 秒）。それを復号の遅れと取り違えない。
        var (monitor, integral, now) = Settled();
        DecodeWindowVerdict[] verdicts = new DecodeWindowVerdict[4];
        for (int i = 0; i < 4; i++)
        {
            integral += Window;
            now += TimeSpan.FromSeconds(Window);
            verdicts[i] = monitor.Observe(40, Window, Fps, disturbances: 1, integral, 40, 600, false, now);
        }

        verdicts.Should().Equal(
            DecodeWindowVerdict.Skipped, DecodeWindowVerdict.Skipped, DecodeWindowVerdict.Skipped,
            DecodeWindowVerdict.Behind);
    }

    [Fact]
    public void PausedOrGapIsNotJudged()
    {
        var (monitor, integral, now) = Settled();

        monitor.Observe(0, Window, Fps, 0, integral + Window, 40, 600, pausedOrInGap: true, now + TimeSpan.FromSeconds(Window))
            .Should().Be(DecodeWindowVerdict.Skipped, "止まっている・ギャップの中はフレームが来ないのが正常");
    }

    [Fact]
    public void NearTheEndIsNotJudged()
    {
        var (monitor, integral, now) = Settled();

        monitor.Observe(10, Window, Fps, 0, integral + Window, 598.5, 600, false, now + TimeSpan.FromSeconds(Window))
            .Should().Be(DecodeWindowVerdict.Skipped, "終端付近でフレームが尽きるのは正常");
    }

    [Fact]
    public void TheFirstWindowIsNeverJudged()
    {
        new DecodeHealthMonitor().Observe(0, Window, Fps, 0, Window, 40, 600, false, T0)
            .Should().Be(DecodeWindowVerdict.Skipped, "比べる前の窓が無い");
    }

    [Fact]
    public void TheMessageStaysForTenSecondsAfterTheLastDrop()
    {
        var (monitor, integral, now) = Settled();
        DateTime dropAt = now + TimeSpan.FromSeconds(Window);
        monitor.Observe(74, Window, Fps, 0, integral + Window, 40, 600, false, dropAt);

        monitor.StatusText(dropAt + TimeSpan.FromSeconds(9)).Should().NotBeEmpty();
        monitor.StatusText(dropAt + TimeSpan.FromSeconds(11)).Should().BeEmpty();
    }

    [Fact]
    public void AWindowRestartedMidwayIsJudgedOverItsOwnSpan()
    {
        // 検証機（M3 ×10、候補 047cand1）: 表示 11 件のうち 10 件が誤検知だった。perf の窓は再生位置が
        // 後ろへ戻ると作り直される（VP9 4K60 の位置の交番で起きる）。速度の積分の差を「前回の判定から」
        // 取っていたので、4〜8 秒ぶんの積分を 2 秒で割り、期待値が膨らんでいた。
        // 実例: 前回の perf 行から 5.57 秒、窓は 2.01 秒・123 枚（比 1.02）なのに「123 / 334」と出た。
        var (monitor, integral, now) = Settled();
        integral += 3.56;
        now += TimeSpan.FromSeconds(3.56);
        monitor.BeginWindow(0, integral);

        DecodeWindowVerdict verdict = monitor.Observe(123, 2.01, Fps, 0, integral + 2.01, 40, 600, false,
            now + TimeSpan.FromSeconds(2.01));

        verdict.Should().Be(DecodeWindowVerdict.Ok, "窓の中では 2.01 秒で 123 枚届いている");
    }

    [Fact]
    public void TheCommandedRateIsAveragedOverTheWindowOnly()
    {
        // 作り直しで捨てた部分の速度（着地窓の 1.2 倍速）を、窓の期待値に混ぜない。
        // 前回の判定からの平均（1.12 倍速）で割ると 120 / 134 = 0.89 で誤って Behind になる。
        var (monitor, integral, now) = Settled();
        integral += 3.0 * 1.2;
        now += TimeSpan.FromSeconds(3.0);
        monitor.BeginWindow(0, integral);

        monitor.Observe(120, Window, Fps, 0, integral + Window, 40, 600, false, now + TimeSpan.FromSeconds(Window))
            .Should().Be(DecodeWindowVerdict.Ok);
    }

    [Fact]
    public void ASeekBeforeTheWindowRestartStillSettles()
    {
        // シークで位置が戻ると、perf の窓はシークの直後に作り直される。乱れはその前（捨てた部分）に
        // あるが、着地の遅れはこれからの窓に出るので、あと 2 窓は判定しない。
        var (monitor, integral, now) = Settled();
        now += TimeSpan.FromSeconds(1);
        integral += 1;
        monitor.BeginWindow(disturbances: 1, integral);
        DecodeWindowVerdict[] verdicts = new DecodeWindowVerdict[3];
        for (int i = 0; i < 3; i++)
        {
            integral += Window;
            now += TimeSpan.FromSeconds(Window);
            verdicts[i] = monitor.Observe(40, Window, Fps, disturbances: 1, integral, 40, 600, false, now);
        }

        verdicts.Should().Equal(DecodeWindowVerdict.Skipped, DecodeWindowVerdict.Skipped, DecodeWindowVerdict.Behind);
    }

    [Fact]
    public void ResetStartsTheCountOver()
    {
        var (monitor, integral, now) = Settled();
        monitor.Observe(74, Window, Fps, 0, integral + Window, 40, 600, false, now + TimeSpan.FromSeconds(Window));
        monitor.BehindCount.Should().Be(1);

        monitor.Reset();

        monitor.BehindCount.Should().Be(0);
        monitor.StatusText(now + TimeSpan.FromSeconds(Window)).Should().BeEmpty();
    }
}
