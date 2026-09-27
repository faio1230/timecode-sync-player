using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.4.8 hotfix: 出力トレースから「同じ絵が続いた区間」を数える監査の判定。
/// 新しいフレームが届いているのに前の絵を出し続けた区間（合成側の停滞）と、配信が止まった区間を分ける。
/// </summary>
public class OutputContinuityAuditTests
{
    private const long Frequency = 1000; // 1 qpc = 1ms

    private static TraceEvent Acquire(long ms, long image, string detail = "Ready") =>
        new("compose.acquire", ms, image, detail);

    private static TraceEvent Delivery(long ms, long image) => new("gst.delivery", ms, image, null);

    [Fact]
    public void NewFrameEveryTick_HasNoHeldSpans()
    {
        var events = Enumerable.Range(1, 120).SelectMany(i => new[]
        {
            Delivery(i * 16 - 5, i),
            Acquire(i * 16, i),
        });

        OutputContinuitySummary summary = OutputContinuityAudit.Summarize(events, Frequency);

        summary.HeldSpans.Should().BeEmpty();
        summary.Deliveries.Should().Be(120);
    }

    [Fact]
    public void HeldWhileFramesWereDelivered_IsCountedWithDeliveries()
    {
        var events = new List<TraceEvent> { Acquire(0, 1) };
        // 16ms ごとに配信は届いているが、合成は 400ms の間ずっと 1 番を採り続けた。
        for (long t = 16; t <= 400; t += 16)
        {
            events.Add(Delivery(t - 5, 1 + t / 16));
            events.Add(Acquire(t, 1));
        }
        events.Add(Acquire(416, 30));

        OutputContinuitySummary summary = OutputContinuityAudit.Summarize(events, Frequency);

        summary.HeldSpans.Should().ContainSingle();
        summary.HeldSpans[0].Seconds.Should().BeApproximately(0.4, 0.001);
        summary.HeldSpans[0].DeliveriesDuring.Should().BeGreaterThan(20);
    }

    [Fact]
    public void HeldBecauseNothingWasDelivered_HasZeroDeliveriesAndADeliveryGap()
    {
        var events = new List<TraceEvent> { Delivery(-5, 1), Acquire(0, 1) };
        for (long t = 16; t <= 300; t += 16)
            events.Add(Acquire(t, 0, "NotReady"));
        events.Add(Delivery(310, 2));
        events.Add(Acquire(316, 2));

        OutputContinuitySummary summary = OutputContinuityAudit.Summarize(events, Frequency);

        summary.HeldSpans.Should().ContainSingle().Which.DeliveriesDuring.Should().Be(0);
        summary.DeliveryGaps.Should().ContainSingle().Which.Seconds.Should().BeApproximately(0.315, 0.001);
    }

    [Fact]
    public void EventsBeforeTheAuditStart_AreIgnored()
    {
        // 起動時のプロジェクト読み込み（一時停止で同じ絵が続く）を数えない。
        var events = new List<TraceEvent> { Acquire(0, 1) };
        for (long t = 16; t <= 1000; t += 16)
            events.Add(Acquire(t, 1));
        for (long i = 1; i <= 60; i++)
        {
            events.Add(Delivery(1000 + i * 16 - 5, 1 + i));
            events.Add(Acquire(1000 + i * 16, 1 + i));
        }

        OutputContinuityAudit.Summarize(events, Frequency).HeldSpans.Should().ContainSingle();
        OutputContinuityAudit.Summarize(events, Frequency, fromQpc: 1010).HeldSpans.Should().BeEmpty();
    }

    [Fact]
    public void ShortHold_BelowThreshold_IsNotReported()
    {
        var events = new List<TraceEvent> { Acquire(0, 1), Acquire(16, 1), Acquire(32, 1), Acquire(48, 2) };

        OutputContinuityAudit.Summarize(events, Frequency).HeldSpans.Should().BeEmpty();
    }

    // ---- トラック切替の除外（切替の発行 → 新しいトラックの最初のフレーム、上限 1.0 秒）----

    private static TraceEvent LoadIssue(long ms) => new("load.issue", ms, 0, "start");

    [Fact]
    public void SwitchLoad_HeldWithinOneSecondAfterTheSwitch_IsExcluded()
    {
        // 切替を発行（50ms）してから最初のフレームが届く（433ms）まで、前の絵が 433ms 続く。
        var events = new List<TraceEvent> { Acquire(0, 1), LoadIssue(50) };
        for (long t = 16; t <= 432; t += 16)
            events.Add(Acquire(t, 1));
        events.Add(Delivery(433, 2));
        events.Add(Acquire(449, 2));   // 届いたフレームを次の tick で採る

        OutputContinuitySummary summary = OutputContinuityAudit.Summarize(events, Frequency);

        summary.HeldSpans.Should().ContainSingle().Which.Seconds.Should().BeApproximately(0.433, 0.001);
        summary.SwitchExclusions.Should().ContainSingle();
        summary.SwitchExclusions[0].Seconds.Should().BeApproximately(0.417, 0.001,
            "除外は発行から最初のフレームの配信 + 採る tick のずれまで");
        summary.SwitchExclusions[0].Capped.Should().BeFalse("最初のフレームが 1.0 秒以内に届いた");
        summary.UnexplainedHeldSpans.Should().BeEmpty("切替の直後 433ms の保持は外れる");
    }

    [Fact]
    public void SwitchLoad_HeldBeyondOneSecond_IsNotExcluded()
    {
        // 切替を発行（50ms）してから 1.2 秒、前の絵が続く。上限を超えた分は除外しない。
        var events = new List<TraceEvent> { Acquire(0, 1), LoadIssue(50) };
        for (long t = 16; t <= 1216; t += 16)
            events.Add(Acquire(t, 1));
        events.Add(Delivery(1216, 2));
        events.Add(Acquire(1232, 2));

        OutputContinuitySummary summary = OutputContinuityAudit.Summarize(events, Frequency);

        summary.HeldSpans.Should().ContainSingle().Which.Seconds.Should().BeApproximately(1.216, 0.001);
        summary.SwitchExclusions.Should().ContainSingle();
        summary.SwitchExclusions[0].Seconds.Should().BeApproximately(1.0, 0.001);
        summary.SwitchExclusions[0].Capped.Should().BeTrue("上限（1.0 秒）で打ち切った");
        summary.UnexplainedHeldSpans.Should().ContainSingle(
            "1.0 秒を超えて同じ絵が続けば U-1 でも FAIL の材料に残る");
    }

    [Fact]
    public void HeldWithoutASwitch_IsNotExcluded()
    {
        var events = new List<TraceEvent> { Acquire(0, 1) };
        for (long t = 16; t <= 400; t += 16)
            events.Add(Acquire(t, 1));
        events.Add(Acquire(416, 2));

        OutputContinuitySummary summary = OutputContinuityAudit.Summarize(events, Frequency);

        summary.SwitchExclusions.Should().BeEmpty("load.issue が無ければ除外は無い");
        summary.UnexplainedHeldSpans.Should().ContainSingle();
    }
}
