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
