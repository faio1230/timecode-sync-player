using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public sealed class LongRunTelemetryAuditTests
{
    [Fact]
    public void PlaybackContinuity_CountsDeficitsAndExcludesTransitions()
    {
        LongRunPerfSample[] samples =
        [
            Perf(2, updates: 60, published: 60),
            Perf(4, updates: 54, published: 114),  // 200ms equivalent deficit
            Perf(6, updates: 45, published: 159),  // transition: excluded
            Perf(8, updates: 42, published: 201),  // 600ms equivalent deficit
        ];

        PlaybackContinuitySummary summary = PlaybackContinuityAudit.Summarize(
            samples, [new LongRunInterval(4.5, 5.5)]);

        summary.TotalSamples.Should().Be(4);
        summary.AuditedSamples.Should().Be(3);
        summary.ExcludedSamples.Should().Be(1);
        summary.DeficitAtLeast100Ms.Should().Be(2);
        summary.DeficitAtLeast250Ms.Should().Be(1);
        summary.DeficitAtLeast500Ms.Should().Be(1);
        summary.MaxDeficitSeconds.Should().BeApproximately(0.6, 0.001);
        summary.GpuRateAuditedSegments.Should().Be(1);
        summary.GpuDeficitAtLeast100Ms.Should().Be(1);
        summary.GpuDeficitAtLeast250Ms.Should().Be(0);
        summary.MaxGpuDeficitSeconds.Should().BeApproximately(0.2, 0.001);
    }

    [Fact]
    public void PlaybackContinuity_SeparatesMissingTelemetryAndGpuPublicationStall()
    {
        LongRunPerfSample[] samples =
        [
            Perf(2, updates: 60, published: 60),
            Perf(9, updates: 60, published: 60),
        ];

        PlaybackContinuitySummary summary = PlaybackContinuityAudit.Summarize(samples);

        summary.TelemetryGaps.Should().Be(1);
        summary.GpuPublicationStalls.Should().Be(1);
        summary.GpuRateAuditedSegments.Should().Be(0);
    }

    [Fact]
    public void PlaybackContinuity_DoesNotCountAWindowThatStartedBeforeTheOrigin()
    {
        // v0.6.4 9-2: 起点（l2-audit-origin）の前に始まり後に終わる窓は、起点の前の準備の区間を含むので数えない。
        // 窓の始まり = 行の時刻 − elapsed。数えなかった窓は excluded に足し、値を残す。
        LongRunPerfSample[] samples =
        [
            Perf(1.2, updates: 30, published: 30),  // 窓 -0.8〜1.2: 起点をまたぐ。30 枚不足（1.0 s）でも数えない
            Perf(3.2, updates: 60, published: 90),
            Perf(5.2, updates: 54, published: 144), // 起点の後に始まる窓の不足（0.2 s）はこれまでどおり数える
        ];

        PlaybackContinuitySummary summary = PlaybackContinuityAudit.Summarize(samples, originSeconds: 0.0);

        summary.TotalSamples.Should().Be(3);
        summary.AuditedSamples.Should().Be(2);
        summary.ExcludedSamples.Should().Be(1);
        summary.MaxDeficitSeconds.Should().BeApproximately(0.2, 0.001);
        summary.DeficitAtLeast100Ms.Should().Be(1);
        summary.DeficitAtLeast500Ms.Should().Be(0);
        summary.PreOriginWindows.Should().ContainSingle();
        LongRunPerfWindow window = summary.PreOriginWindows[0];
        window.StartSeconds.Should().BeApproximately(-0.8, 1e-9);
        window.EndSeconds.Should().BeApproximately(1.2, 1e-9);
        window.FrameUpdates.Should().Be(30);
        window.ExpectedFrames.Should().BeApproximately(60.0, 1e-9);
    }

    [Fact]
    public void PlaybackContinuity_AWindowStartingAtTheOriginIsCounted()
    {
        LongRunPerfSample[] samples = [Perf(2.0, updates: 42, published: 42)];

        PlaybackContinuitySummary summary = PlaybackContinuityAudit.Summarize(samples, originSeconds: 0.0);

        summary.AuditedSamples.Should().Be(1);
        summary.PreOriginWindows.Should().BeEmpty();
        summary.MaxDeficitSeconds.Should().BeApproximately(0.6, 0.001);
    }

    [Fact]
    public void PlaybackContinuity_MeasuresGpuPublicationRateBetweenPerfSamples()
    {
        LongRunPerfSample[] samples =
        [
            Perf(2, updates: 60, published: 60),
            Perf(4, updates: 60, published: 108), // 30fps x 2s に対して12フレーム不足
        ];

        PlaybackContinuitySummary summary = PlaybackContinuityAudit.Summarize(samples);

        summary.GpuRateAuditedSegments.Should().Be(1);
        summary.GpuDeficitAtLeast100Ms.Should().Be(1);
        summary.GpuDeficitAtLeast250Ms.Should().Be(1);
        summary.GpuDeficitAtLeast500Ms.Should().Be(0);
        summary.MaxGpuDeficitSeconds.Should().BeApproximately(0.4, 0.001);
        summary.TotalGpuDeficitSeconds.Should().BeApproximately(0.4, 0.001);
    }

    [Fact]
    public void PositionContinuity_JoinsConsecutiveStoppedPairs()
    {
        LongRunProgressSample[] samples =
        [
            new(0.00, 0, 10.00, 10.00),
            new(0.20, 0, 10.20, 10.00),
            new(0.40, 0, 10.40, 10.01),
            new(0.60, 0, 10.60, 10.02),
            new(0.80, 0, 10.80, 10.80),
        ];

        PositionContinuitySummary summary = PositionContinuityAudit.Summarize(samples);

        summary.Stalls.Should().ContainSingle();
        summary.MaxSeconds.Should().BeApproximately(0.6, 0.001);
        summary.AtLeast500Ms.Should().Be(1);
    }

    [Fact]
    public void PositionContinuity_DoesNotJoinAcrossTrackOrLongSamplingGap()
    {
        LongRunProgressSample[] samples =
        [
            new(0.0, 0, 10.0, 10.0),
            new(0.3, 1, 10.3, 10.0),
            new(2.0, 1, 12.0, 10.0),
        ];

        PositionContinuityAudit.Summarize(samples).Stalls.Should().BeEmpty();
    }

    [Fact]
    public void NativeLoadTimingParser_DropsConfidentialPath()
    {
        const string secret = @"X:\redacted\asset.mov";
        string[] lines =
        [
            $"[tcs-gst] load.attempt path={secret} paused=0 attempt=2 profile=decodebin-fallback result=ok " +
            "teardown_ms=10.0 build_ms=20.0 set_state_ms=30.0 preroll_ms=40.0 first_frame_ms=50.0 " +
            "audio_prime_ms=60.0 pause_ms=70.0 seek_ms=80.0 duration_ms=90.0 total_ms=450.0 frames=3",
            $"[tcs-gst] load.summary path={secret} paused=0 total_ms=455.0 attempt=2 profile=decodebin-fallback",
        ];

        NativeLoadTiming timing = NativeLoadTimingParser.Parse(lines).Should().ContainSingle().Subject;

        timing.TotalMilliseconds.Should().Be(455.0);
        timing.FirstFrameMilliseconds.Should().Be(50.0);
        JsonSerializer.Serialize(timing).Should().NotContain("redacted").And.NotContain("asset.mov");
    }

    [Fact]
    public void ProcessHealthAudit_ReportsPeaksStreakAndRobustGrowth()
    {
        ProcessHealthSample[] samples = Enumerable.Range(0, 10)
            .Select(index => Health(
                at: index * 60,
                responding: index is not (4 or 5),
                privateMb: 100 + index * 2,
                handles: 200 + index))
            .ToArray();

        ProcessHealthSummary summary = ProcessHealthAudit.Summarize(
            samples, warmupSeconds: 0, minimumTrendSeconds: 60);

        summary.MaxUnresponsiveStreak.Should().Be(2);
        summary.PeakPrivateMemoryBytes.Should().Be(118L * 1024 * 1024);
        summary.PeakHandleCount.Should().Be(209);
        summary.TrendMeasurable.Should().BeTrue();
        summary.PrivateGrowthMbPerHour.Should().BeApproximately(120.0, 0.001);
        summary.HandleGrowthPerHour.Should().BeApproximately(60.0, 0.001);
    }

    [Fact]
    public void ProcessHealthMonitor_WritesSamplesIncrementallyWithoutPaths()
    {
        string outputPath = Path.GetTempFileName();
        try
        {
            IReadOnlyList<ProcessHealthSample> samples;
            using (var monitor = new ProcessHealthMonitor(
                       Process.GetCurrentProcess(), TimeSpan.FromMilliseconds(20), outputPath))
            {
                Thread.Sleep(80);
                samples = monitor.Stop();
            }
            string[] lines = File.ReadAllLines(outputPath);

            samples.Count.Should().BeGreaterThanOrEqualTo(2);
            lines.Length.Should().Be(samples.Count);
            lines.Should().OnlyContain(line => !line.Contains("path", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static LongRunPerfSample Perf(double at, int updates, long published) =>
        new(at, 2.0, 30.0, 1.0, updates, published, 0, true);

    private static ProcessHealthSample Health(
        double at, bool responding, long privateMb, int handles) =>
        new(at, true, false, null, responding,
            privateMb * 1024 * 1024, privateMb * 1024 * 1024,
            handles, 20, 10.0);
}
