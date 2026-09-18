using FluentAssertions;
using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer.Tests;

public class LongGopWarningMonitorTests
{
    private static GopStatus Status(
        int state,
        bool active = true,
        double maxIntervalSeconds = 0,
        double pendingSeconds = 0,
        double thresholdSeconds = 3.0) =>
        new(state, active, 0, maxIntervalSeconds, pendingSeconds, thresholdSeconds, 0);

    [Fact]
    public void Observe_WithoutStatus_DoesNotWarn()
    {
        var monitor = new LongGopWarningMonitor();
        Guid track = Guid.NewGuid();

        monitor.Observe(track, null, trackAlreadyMarked: false)
            .Should().Be(LongGopWarningTransition.None);
        monitor.IsWarningActive.Should().BeFalse();
    }

    [Fact]
    public void Observe_ShortGop_NeverWarns()
    {
        var monitor = new LongGopWarningMonitor();
        Guid track = Guid.NewGuid();

        for (int i = 0; i < 20; i++)
        {
            monitor.Observe(track, Status(LongGopWarningMonitor.StateMeasuring, maxIntervalSeconds: 1.0),
                trackAlreadyMarked: false).Should().Be(LongGopWarningTransition.None);
        }

        monitor.IsWarningActive.Should().BeFalse();
        monitor.MeasuredSeconds.Should().Be(1.0);
    }

    [Fact]
    public void Observe_WarningState_LatchesOnceAndKeepsDisplaying()
    {
        var monitor = new LongGopWarningMonitor();
        Guid track = Guid.NewGuid();

        monitor.Observe(track, Status(LongGopWarningMonitor.StateWarning, pendingSeconds: 3.2),
            trackAlreadyMarked: false).Should().Be(LongGopWarningTransition.Latch);
        monitor.IsWarningActive.Should().BeTrue();

        monitor.Observe(track, Status(LongGopWarningMonitor.StateWarning, pendingSeconds: 4.0),
            trackAlreadyMarked: false).Should().Be(LongGopWarningTransition.None,
            "一度ラッチしたら再通知しない");

        // 再ロードで shim が measuring に戻っても表示は消えない。
        monitor.Observe(track, Status(LongGopWarningMonitor.StateMeasuring),
            trackAlreadyMarked: true).Should().Be(LongGopWarningTransition.None);
        monitor.IsWarningActive.Should().BeTrue("リロード・シークで消えない");
    }

    [Fact]
    public void Observe_ConfirmedIntervalAfterLatch_RaisesIntervalUpdated()
    {
        var monitor = new LongGopWarningMonitor();
        Guid track = Guid.NewGuid();

        monitor.Observe(track, Status(LongGopWarningMonitor.StateWarning, pendingSeconds: 3.2),
            trackAlreadyMarked: false).Should().Be(LongGopWarningTransition.Latch);
        monitor.MeasuredSeconds.Should().Be(0, "未確定のうちは 0");

        monitor.Observe(track, Status(LongGopWarningMonitor.StateWarning, maxIntervalSeconds: 10.1),
            trackAlreadyMarked: false).Should().Be(LongGopWarningTransition.IntervalUpdated);
        monitor.MeasuredSeconds.Should().Be(10.1);

        monitor.Observe(track, Status(LongGopWarningMonitor.StateWarning, maxIntervalSeconds: 10.1),
            trackAlreadyMarked: false).Should().Be(LongGopWarningTransition.None);
    }

    [Fact]
    public void Observe_WarningWithConfirmedInterval_LatchCarriesMeasurement()
    {
        var monitor = new LongGopWarningMonitor();
        Guid track = Guid.NewGuid();

        monitor.Observe(track, Status(LongGopWarningMonitor.StateWarning, maxIntervalSeconds: 10.1),
            trackAlreadyMarked: false).Should().Be(LongGopWarningTransition.Latch);
        monitor.MeasuredSeconds.Should().Be(10.1);
        monitor.IsWarningActive.Should().BeTrue();
    }

    [Fact]
    public void Observe_AlreadyMarkedTrack_StartsDisplayingWithoutReLatch()
    {
        var monitor = new LongGopWarningMonitor();
        Guid track = Guid.NewGuid();

        monitor.Observe(track, Status(LongGopWarningMonitor.StateMeasuring),
            trackAlreadyMarked: true).Should().Be(LongGopWarningTransition.None);
        monitor.IsWarningActive.Should().BeTrue("プレイリストの印で即表示");
        monitor.MeasuredSeconds.Should().Be(0);

        monitor.Observe(track, Status(LongGopWarningMonitor.StateWarning, maxIntervalSeconds: 10.1),
            trackAlreadyMarked: true).Should().Be(LongGopWarningTransition.IntervalUpdated);
    }

    [Fact]
    public void Observe_TrackSwitch_ResetsObservation()
    {
        var monitor = new LongGopWarningMonitor();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        monitor.Observe(first, Status(LongGopWarningMonitor.StateWarning, maxIntervalSeconds: 10.1),
            trackAlreadyMarked: false).Should().Be(LongGopWarningTransition.Latch);
        monitor.IsWarningActive.Should().BeTrue();

        monitor.Observe(second, Status(LongGopWarningMonitor.StateMeasuring),
            trackAlreadyMarked: false).Should().Be(LongGopWarningTransition.None);
        monitor.TrackId.Should().Be(second);
        monitor.IsWarningActive.Should().BeFalse("別トラックの警告は表示しない");
        monitor.MeasuredSeconds.Should().Be(0);

        monitor.Observe(second, Status(LongGopWarningMonitor.StateWarning),
            trackAlreadyMarked: false).Should().Be(LongGopWarningTransition.Latch,
            "トラック切替後は新しいトラックでラッチできる");
    }

    [Fact]
    public void Observe_WithoutActiveProbe_ShowsNothing()
    {
        var monitor = new LongGopWarningMonitor();
        Guid track = Guid.NewGuid();

        monitor.Observe(track, Status(LongGopWarningMonitor.StateWarning, active: false),
            trackAlreadyMarked: false).Should().Be(LongGopWarningTransition.None);
        monitor.IsWarningActive.Should().BeFalse("未計測は異常なしではない");
    }

    [Fact]
    public void Observe_NullTrack_ClearsDisplay()
    {
        var monitor = new LongGopWarningMonitor();
        Guid track = Guid.NewGuid();

        monitor.Observe(track, Status(LongGopWarningMonitor.StateWarning),
            trackAlreadyMarked: false).Should().Be(LongGopWarningTransition.Latch);

        monitor.Observe(null, null, trackAlreadyMarked: false)
            .Should().Be(LongGopWarningTransition.None);
        monitor.IsWarningActive.Should().BeFalse();
        monitor.TrackId.Should().BeNull();
    }

    [Fact]
    public void Messages_FormatIncludesMeasuredSecondsOnlyWhenKnown()
    {
        LongGopWarningMessages.Format(0)
            .Should().Be(LongGopWarningMessages.Recommendation);
        LongGopWarningMessages.Format(10.1)
            .Should().Be(LongGopWarningMessages.Recommendation + "（実測 10.1 秒）");
    }
}
