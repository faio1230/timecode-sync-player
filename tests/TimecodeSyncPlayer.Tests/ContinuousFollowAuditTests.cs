using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>L-1: 連続追従の窓集計（純関数）の固定。</summary>
public sealed class ContinuousFollowAuditTests
{
    private static readonly Func<double, double> Identity = ltc => ltc;

    private static List<FollowSample> Samples(double seconds, double step, Func<double, double> position)
    {
        var samples = new List<FollowSample>();
        int count = (int)Math.Floor(seconds / step);
        for (int i = 0; i < count; i++)
        {
            double t = i * step;
            samples.Add(new FollowSample(t, 10.0 + t, position(t)));
        }
        return samples;
    }

    private static List<FollowPerfSegment> Perfs(double seconds, double window, Func<int, int> updates)
    {
        var segments = new List<FollowPerfSegment>();
        int index = 0;
        for (double at = window; at <= seconds; at += window)
            segments.Add(new FollowPerfSegment(at, window, updates(index++)));
        return segments;
    }

    [Fact]
    public void Summarize_HealthyFollow_ReportsNoStalls()
    {
        List<FollowSample> samples = Samples(10.0, 0.2, t => 10.0 + t + 0.01);
        List<FollowPerfSegment> perf = Perfs(10.0, 2.0, _ => 60);

        ContinuousFollowSummary summary = ContinuousFollowAudit.Summarize(
            samples, perf, durationSeconds: 10.0, windowSeconds: 2.0, expectedPosition: Identity);

        summary.Windows.Should().HaveCount(5);
        summary.StallUpdateWindows.Should().Be(0);
        summary.StallAdvanceWindows.Should().Be(0);
        summary.MeanFrameUpdates.Should().BeApproximately(60.0, 0.001);
        summary.MaxAbsError.Should().BeApproximately(0.01, 0.001);
        summary.WorstUpdates!.Value.FrameUpdates.Should().Be(60);
        summary.WorstAdvance!.Value.PositionAdvance.Should().BeGreaterThan(1.5);
    }

    [Fact]
    public void Summarize_ZeroUpdateWindow_IsCountedAsStalled()
    {
        List<FollowSample> samples = Samples(8.0, 0.2, t => 10.0 + t);
        List<FollowPerfSegment> perf =
        [
            new FollowPerfSegment(2.0, 2.0, 60),
            new FollowPerfSegment(4.0, 2.0, 0),   // 3 本目の窓（2.0〜4.0）で 0 更新
            new FollowPerfSegment(6.0, 2.0, 60),
            new FollowPerfSegment(8.0, 2.0, 60),
        ];

        ContinuousFollowSummary summary = ContinuousFollowAudit.Summarize(
            samples, perf, durationSeconds: 8.0, windowSeconds: 2.0, expectedPosition: Identity);

        summary.Windows.Should().HaveCount(4);
        summary.StallUpdateWindows.Should().Be(1);
        summary.WorstUpdates!.Value.Index.Should().Be(1);
        summary.StallAdvanceWindows.Should().Be(0);
    }

    [Fact]
    public void Summarize_WindowWithoutAnyPerfLine_IsTreatedAsStalled()
    {
        // フレームが止まると Playback perf 行自体が出ない。行が無い窓は更新 0 として数える。
        List<FollowSample> samples = Samples(6.0, 0.2, t => 10.0 + t);
        List<FollowPerfSegment> perf =
        [
            new FollowPerfSegment(2.0, 2.0, 60),
            new FollowPerfSegment(6.0, 2.0, 60),   // 2.0〜4.0 の行が無い
        ];

        ContinuousFollowSummary summary = ContinuousFollowAudit.Summarize(
            samples, perf, durationSeconds: 6.0, windowSeconds: 2.0, expectedPosition: Identity);

        summary.StallUpdateWindows.Should().Be(1);
        summary.WorstUpdates!.Value.Index.Should().Be(1);
    }

    [Fact]
    public void Summarize_NoPositionAdvance_IsCountedAsStalled()
    {
        List<FollowSample> samples = Samples(6.0, 0.2, t => t < 2.0 || t >= 4.0 ? 10.0 + t : 12.0);
        List<FollowPerfSegment> perf = Perfs(6.0, 2.0, _ => 60);

        ContinuousFollowSummary summary = ContinuousFollowAudit.Summarize(
            samples, perf, durationSeconds: 6.0, windowSeconds: 2.0, expectedPosition: Identity);

        summary.StallAdvanceWindows.Should().Be(1);
        summary.WorstAdvance!.Value.Index.Should().Be(1);
        summary.StallUpdateWindows.Should().Be(0);
    }

    [Fact]
    public void Summarize_ErrorIsMaxPerWindow_AndWorstWindowIsReported()
    {
        List<FollowSample> samples = Samples(6.0, 0.2, t => t < 4.0 ? 10.0 + t : 10.0 + t + 0.5);
        List<FollowPerfSegment> perf = Perfs(6.0, 2.0, _ => 60);

        ContinuousFollowSummary summary = ContinuousFollowAudit.Summarize(
            samples, perf, durationSeconds: 6.0, windowSeconds: 2.0, expectedPosition: Identity);

        summary.Windows[0].MaxAbsError.Should().BeApproximately(0.0, 0.001);
        summary.Windows[2].MaxAbsError.Should().BeApproximately(0.5, 0.001);
        summary.MaxAbsError.Should().BeApproximately(0.5, 0.001);
        summary.WorstError!.Value.Index.Should().Be(2);
    }

    [Fact]
    public void Summarize_NonFiniteSamples_AreIgnoredForErrorAndAdvance()
    {
        List<FollowSample> samples =
        [
            new FollowSample(0.0, 10.0, 10.0),
            new FollowSample(0.5, double.NaN, 10.5),
            new FollowSample(1.0, 11.0, double.NaN),
            new FollowSample(1.5, 11.5, 11.5),
        ];
        List<FollowPerfSegment> perf = [new FollowPerfSegment(2.0, 2.0, 60)];

        ContinuousFollowSummary summary = ContinuousFollowAudit.Summarize(
            samples, perf, durationSeconds: 2.0, windowSeconds: 2.0, expectedPosition: Identity);

        summary.Windows.Should().HaveCount(1);
        summary.MaxAbsError.Should().Be(0.0);
        summary.Windows[0].PositionAdvance.Should().BeApproximately(1.5, 0.001);
        summary.StallAdvanceWindows.Should().Be(0);
    }

    [Fact]
    public void Summarize_ShorterThanOneWindow_ReturnsNoWindows()
    {
        List<FollowSample> samples = Samples(1.0, 0.2, t => 10.0 + t);

        ContinuousFollowSummary summary = ContinuousFollowAudit.Summarize(
            samples, [], durationSeconds: 1.0, windowSeconds: 2.0, expectedPosition: Identity);

        summary.Windows.Should().BeEmpty();
        summary.StallUpdateWindows.Should().Be(0);
        summary.StallAdvanceWindows.Should().Be(0);
        summary.MeanFrameUpdates.Should().Be(0.0);
    }
}
