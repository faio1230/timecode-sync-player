using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>起動直後の UI スレッドの生存記録（区間の始まり・終わり、区間の外、遅れの計算）。</summary>
public class UiHeartbeatRecorderTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void BeforeStart_WritesNothing()
    {
        var lines = new List<string>();
        var recorder = new UiHeartbeatRecorder(lines.Add);

        recorder.Tick(Ms(100)).Should().BeFalse();
        recorder.Stop(Ms(200), "closing");

        lines.Should().BeEmpty("区間の外では出さない");
    }

    [Fact]
    public void Start_WritesTheStartLine()
    {
        var lines = new List<string>();
        var recorder = new UiHeartbeatRecorder(lines.Add);

        recorder.Start(Ms(1000));

        lines.Should().Equal("start intervalMs=100.0 windowMs=30000.0");
        recorder.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Tick_LateIsMeasuredFromThePreviousTickPlusTheInterval()
    {
        var lines = new List<string>();
        var recorder = new UiHeartbeatRecorder(lines.Add);
        recorder.Start(Ms(0));

        recorder.Tick(Ms(100)).Should().BeTrue();   // 予定どおり
        recorder.Tick(Ms(350)).Should().BeTrue();   // 予定 200 → 150ms 遅れ
        recorder.Tick(Ms(452.5)).Should().BeTrue(); // 予定 450 → 2.5ms 遅れ（直前の tick が基準）

        lines.Skip(1).Should().Equal("seq=1 lateMs=0.0", "seq=2 lateMs=150.0", "seq=3 lateMs=2.5");
    }

    [Fact]
    public void WindowElapsed_WritesTheEndLine_AndStops()
    {
        var lines = new List<string>();
        var recorder = new UiHeartbeatRecorder(lines.Add);
        recorder.Start(Ms(0));
        recorder.Tick(Ms(100));
        recorder.Tick(Ms(1300)); // 1100ms 遅れ（UI スレッドが止まっていた）

        recorder.Tick(UiHeartbeatRecorder.Window).Should().BeFalse("区間の後はタイマーを捨てる");

        lines.Last().Should().Be("end reason=window ticks=3 maxLateMs=28600.0 elapsedMs=30000.0");
        recorder.IsActive.Should().BeFalse();
    }

    [Fact]
    public void AfterTheEnd_WritesNothing()
    {
        var lines = new List<string>();
        var recorder = new UiHeartbeatRecorder(lines.Add);
        recorder.Start(Ms(0));
        recorder.Tick(UiHeartbeatRecorder.Window);
        int count = lines.Count;

        recorder.Tick(UiHeartbeatRecorder.Window + Ms(100)).Should().BeFalse();
        recorder.Stop(UiHeartbeatRecorder.Window + Ms(200), "closing");
        recorder.Start(UiHeartbeatRecorder.Window + Ms(300));

        lines.Should().HaveCount(count, "区間の外（終わった後）では出さず、始め直さない");
    }

    [Fact]
    public void StopDuringTheWindow_WritesTheEndLineOnce()
    {
        var lines = new List<string>();
        var recorder = new UiHeartbeatRecorder(lines.Add);
        recorder.Start(Ms(0));
        recorder.Tick(Ms(120));

        recorder.Stop(Ms(500), "closing");
        recorder.Stop(Ms(600), "closing");

        lines.Should().Equal(
            "start intervalMs=100.0 windowMs=30000.0",
            "seq=1 lateMs=20.0",
            "end reason=closing ticks=1 maxLateMs=20.0 elapsedMs=500.0");
    }
}
