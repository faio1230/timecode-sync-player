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
}
