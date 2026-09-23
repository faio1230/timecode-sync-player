using System.Diagnostics;
using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests.Output;

/// <summary>0.4.8: 前の絵を出した合成 tick を理由ごとに数える（テスト 8 の通常ログ側）。</summary>
public class OutputHoldCounterTests
{
    private static long Ms(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);

    [Fact]
    public void NewFrameEveryTick_HasNoHeld()
    {
        var c = new OutputHoldCounter();
        for (int i = 1; i <= 120; i++)
            c.RecordTick(drewNewFrame: true, fencePending: false, Ms(i * 16.7));

        OutputHoldSnapshot s = c.Take(Ms(2000));
        s.NewFrameTicks.Should().Be(120);
        s.FencePendingTicks.Should().Be(0);
        s.NoNewFrameTicks.Should().Be(0);
        s.LongestHeldMs.Should().Be(0);
    }

    [Fact]
    public void HeldSpans_AreMeasuredAndSplitByReason()
    {
        var c = new OutputHoldCounter();
        c.RecordTick(true, false, Ms(0));
        for (int i = 1; i <= 24; i++)                       // 400ms フェンス待ち
            c.RecordTick(false, fencePending: true, Ms(i * 16.7));
        c.RecordFenceWait(400);
        c.RecordTick(true, false, Ms(25 * 16.7));
        for (int i = 26; i <= 31; i++)                      // 100ms 新しいフレーム無し
            c.RecordTick(false, fencePending: false, Ms(i * 16.7));
        c.RecordTick(true, false, Ms(32 * 16.7));

        OutputHoldSnapshot s = c.Take(Ms(600));
        s.FencePendingTicks.Should().Be(24);
        s.NoNewFrameTicks.Should().Be(6);
        s.LongestHeldMs.Should().BeApproximately(24 * 16.7, 0.5);
        s.MaxFenceWaitMs.Should().Be(400);
    }

    [Fact]
    public void ExcludedTicks_EndTheHeldSpan_AndTakeResets()
    {
        var c = new OutputHoldCounter();
        c.RecordTick(false, false, Ms(0));
        c.RecordTick(false, false, Ms(50));
        c.RecordExcludedTick(Ms(80));                       // ギャップに入った
        c.RecordExcludedTick(Ms(900));

        c.Take(Ms(1000)).LongestHeldMs.Should().BeApproximately(80, 0.5, "ギャップの静止は数えない");
        c.Take(Ms(2000)).Should().Be(new OutputHoldSnapshot(0, 0, 0, 0, 0));
    }

    [Fact]
    public void OngoingHeld_IsReportedUpToNow_AndContinuesIntoTheNextWindow()
    {
        var c = new OutputHoldCounter();
        c.RecordTick(false, true, Ms(0));
        c.Take(Ms(300)).LongestHeldMs.Should().BeApproximately(300, 0.5);
        c.RecordTick(false, true, Ms(400));
        c.Take(Ms(500)).LongestHeldMs.Should().BeApproximately(200, 0.5, "窓の境目からの長さ");
    }
}
