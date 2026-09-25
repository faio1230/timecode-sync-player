using System.Diagnostics;
using System.Globalization;
using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// Production-day soak: keep one app process alive across rehearsal, an LTC-off
/// break, the show, and the post-show idle period.
/// </summary>
public sealed partial class LtcScenarioE2ETests
{
    private const string L3RehearsalVariable = "TCS_L3_REHEARSAL_SECONDS";
    private const string L3BreakVariable = "TCS_L3_BREAK_SECONDS";
    private const string L3ShowVariable = "TCS_L3_SHOW_SECONDS";
    private const string L3PostIdleVariable = "TCS_L3_POST_IDLE_SECONDS";

    [SkippableFact(Timeout = 46_800_000)]
    public void L3_ProductionDay_KeepsOneProcessStableAcrossRehearsalBreakAndShow() =>
        Run("L-3", continueMode: true, blackGap: true, scenario =>
        {
            double rehearsalSeconds = L3DurationFromEnvironment(L3RehearsalVariable, 4 * 3600.0);
            double breakSeconds = L3DurationFromEnvironment(L3BreakVariable, 2 * 3600.0);
            double showSeconds = L3DurationFromEnvironment(L3ShowVariable, 4 * 3600.0);
            double postIdleSeconds = L3DurationFromEnvironment(L3PostIdleVariable, 2 * 3600.0);
            double activeSeconds = rehearsalSeconds + showSeconds;
            double totalSeconds = activeSeconds + breakSeconds + postIdleSeconds;
            double lapSeconds = scenario.Tracks.Max(track => track.End);
            double healthIntervalSeconds = L2PositiveDoubleFromEnvironment(
                "TCS_L2_HEALTH_SAMPLE_SECONDS", 10.0);
            double maxFrameDeficitSeconds = L2PositiveDoubleFromEnvironment(
                "TCS_L2_MAX_FRAME_DEFICIT_SECONDS", 0.5);
            double maxPositionStallSeconds = L2PositiveDoubleFromEnvironment(
                "TCS_L2_MAX_POSITION_STALL_SECONDS", 0.5);
            double maxSpoutReceiverGapMilliseconds = L2PositiveDoubleFromEnvironment(
                "TCS_L2_MAX_SPOUT_RECEIVER_GAP_MS", 500.0);
            double? maxPrivateGrowthMbPerHour = L2OptionalPositiveDoubleFromEnvironment(
                "TCS_L2_MAX_PRIVATE_GROWTH_MB_PER_HOUR");
            double? maxHandleGrowthPerHour = L2OptionalPositiveDoubleFromEnvironment(
                "TCS_L2_MAX_HANDLE_GROWTH_PER_HOUR");

            Skip.If(lapSeconds < 10.0, $"L-3: プレイリストが {lapSeconds:F1}s しかない");
            scenario.SetSync(true);
            bool spoutRequired = L2BooleanFromEnvironment("TCS_L2_ENABLE_SPOUT");
            if (spoutRequired)
                scenario.SetSpout(true);

            scenario.Journal.Write("l3-plan", details: new
            {
                ltcFps = LtcFps,
                rehearsalSeconds,
                breakSeconds,
                showSeconds,
                postIdleSeconds,
                activeSeconds,
                totalSeconds,
                lapSeconds = Math.Round(lapSeconds, 3),
                spoutRequired,
                externalSpoutReceiverRequired = ExternalSpoutReceiverMonitor.IsRequired,
            });

            DateTime startedAt = DateTime.Now;
            var clock = Stopwatch.StartNew();
            using var healthMonitor = new ProcessHealthMonitor(
                scenario.App.Process,
                TimeSpan.FromSeconds(healthIntervalSeconds),
                Path.Combine(scenario.ReportDir, "process-health.jsonl"));
            if (ExternalSpoutReceiverMonitor.IsRequired && !spoutRequired)
                throw new InvalidOperationException("External Spout receiver audit requires Spout ON.");
            using ExternalSpoutReceiverMonitor? spoutReceiver = spoutRequired
                ? ExternalSpoutReceiverMonitor.StartFromEnvironment(scenario.ReportDir, totalSeconds)
                : null;
            if (ExternalSpoutReceiverMonitor.IsRequired && spoutReceiver is null)
                throw new InvalidOperationException("External Spout receiver audit was required but not configured.");
            var exclusions = new List<LongRunInterval>();
            var progress = new List<LongRunProgressSample>();
            var errors = new List<double>();
            var phases = new List<L3PhaseResult>();

            phases.Add(L3RunActivePhase(
                scenario, "rehearsal", rehearsalSeconds, lapSeconds, clock,
                exclusions, progress, errors));
            L3RunIdlePhase(scenario, "break", breakSeconds, clock, exclusions);
            phases.Add(L3RunActivePhase(
                scenario, "show", showSeconds, lapSeconds, clock,
                exclusions, progress, errors));
            L3RunIdlePhase(scenario, "post-idle", postIdleSeconds, clock, exclusions);

            Thread.Sleep(2200);
            IReadOnlyList<ProcessHealthSample> healthSamples = healthMonitor.Stop();
            List<LongRunPerfSample> perf = scenario.LongRunPerfSince(startedAt).ToList();
            PlaybackContinuitySummary playback = PlaybackContinuityAudit.Summarize(perf, exclusions);
            PositionContinuitySummary position = PositionContinuityAudit.Summarize(progress);
            ProcessHealthSummary health = ProcessHealthAudit.Summarize(
                healthSamples, warmupSeconds: 1800.0, minimumTrendSeconds: 60.0);
            bool spoutUiOn = scenario.SpoutIsOn();
            int spoutOffLogLines = scenario.CountLogMatchesSince(@"Spout 出力: OFF", startedAt);
            long sentFrames = phases.Sum(phase => phase.SentFrames);
            long plannedFrames = phases.Sum(phase => phase.PlannedFrames);
            int switches = phases.Sum(phase => phase.ZoneChanges);
            double[] sortedErrors = errors.OrderBy(value => value).ToArray();
            ExternalSpoutReceiverSummary? spoutReceiverSummary = spoutReceiver?.Stop();

            scenario.Journal.Write("l3-summary", details: new
            {
                wallSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
                activeSeconds,
                idleSeconds = breakSeconds + postIdleSeconds,
                ltcSentFrames = sentFrames,
                ltcPlannedFrames = plannedFrames,
                switches,
                phaseSamples = phases.Select(phase => new
                {
                    phase.Name,
                    phase.Samples,
                    phase.ZoneChanges,
                    phase.SentFrames,
                    phase.PlannedFrames,
                }),
                playbackSamples = playback.AuditedSamples,
                maxFrameDeficitSeconds = Math.Round(playback.MaxDeficitSeconds, 3),
                gpuRateAuditedSegments = playback.GpuRateAuditedSegments,
                gpuDeficits500Ms = playback.GpuDeficitAtLeast500Ms,
                maxGpuDeficitSeconds = Math.Round(playback.MaxGpuDeficitSeconds, 3),
                playback.TelemetryGaps,
                playback.GpuPublicationStalls,
                positionSamples = progress.Count,
                maxPositionStallSeconds = Math.Round(position.MaxSeconds, 3),
                errorSamples = sortedErrors.Length,
                errorP99Seconds = Math.Round(L2Percentile(sortedErrors, 0.99), 4),
                healthSamples = health.Samples,
                health.UnavailableSamples,
                health.ExitedSamples,
                health.MaxUnresponsiveStreak,
                peakPrivateMb = Math.Round(health.PeakPrivateMemoryBytes / 1024.0 / 1024.0, 1),
                peakWorkingSetMb = Math.Round(health.PeakWorkingSetBytes / 1024.0 / 1024.0, 1),
                health.PeakHandleCount,
                health.PeakThreadCount,
                privateGrowthMbPerHour = Math.Round(health.PrivateGrowthMbPerHour, 2),
                handleGrowthPerHour = Math.Round(health.HandleGrowthPerHour, 2),
                spoutUiOn,
                spoutOffLogLines,
                externalSpoutReceiver = spoutReceiverSummary is not null,
                spoutReceiverCompleted = spoutReceiverSummary?.CompletedNormally,
                spoutReceiverFrames = spoutReceiverSummary?.UniqueFrames,
                spoutReceiverMissedFrames = spoutReceiverSummary?.MissedSenderFrames,
                spoutReceiverMaxUnchangedMs = spoutReceiverSummary is null
                    ? (double?)null : Math.Round(spoutReceiverSummary.MaxUnchangedMilliseconds, 3),
                spoutReceiverMaxNonBlackUnchangedMs = spoutReceiverSummary is null
                    ? (double?)null : Math.Round(spoutReceiverSummary.MaxNonBlackUnchangedMilliseconds, 3),
                spoutReceiverGaps100Ms = spoutReceiverSummary?.GapsAtLeast100Ms,
                spoutReceiverGaps250Ms = spoutReceiverSummary?.GapsAtLeast250Ms,
                spoutReceiverGaps500Ms = spoutReceiverSummary?.GapsAtLeast500Ms,
                maxSpoutReceiverGapMs = spoutReceiverSummary is null
                    ? (double?)null : Math.Round(spoutReceiverSummary.MaxGapMilliseconds, 3),
                maxSpoutReceiveMs = spoutReceiverSummary is null
                    ? (double?)null : Math.Round(spoutReceiverSummary.MaxReceiveMilliseconds, 3),
                maxSpoutGpuReadbackMs = spoutReceiverSummary is null
                    ? (double?)null : Math.Round(spoutReceiverSummary.MaxGpuReadbackMilliseconds, 3),
            });

            long requiredFrames = (long)Math.Floor(activeSeconds * LtcFps);
            sentFrames.Should().BeGreaterThanOrEqualTo(requiredFrames,
                $"L-3: rehearsal/show LTC completes ({sentFrames}/{requiredFrames}, planned {plannedFrames})");
            phases.Should().OnlyContain(phase => phase.Samples > 0,
                "L-3: both rehearsal and show yield observable LTC/playback samples");
            phases.Should().OnlyContain(phase => phase.ZoneChanges > 0,
                "L-3: the playlist keeps switching during both rehearsal and show");
            sortedErrors.Should().NotBeEmpty("L-3: stable active intervals yield sync-error samples");
            L2Percentile(sortedErrors, 0.99).Should().BeLessThanOrEqualTo(PositionToleranceSeconds,
                "L-3: active-phase sync error p99 remains within tolerance after settling");
            playback.AuditedSamples.Should().BeGreaterThan(0,
                "L-3: active intervals contain Playback perf samples");
            playback.MaxDeficitSeconds.Should().BeLessThan(maxFrameDeficitSeconds,
                $"L-3: active-phase frame deficit stays below {maxFrameDeficitSeconds:F3}s");
            playback.TelemetryGaps.Should().Be(0,
                "L-3: Playback perf does not disappear during non-idle intervals");
            playback.GpuPublicationStalls.Should().Be(0,
                "L-3: GPU publication continues during stable active intervals");
            playback.GpuRateAuditedSegments.Should().BeGreaterThan(0,
                "L-3: active intervals contain adjacent GPU publication samples");
            playback.MaxGpuDeficitSeconds.Should().BeLessThan(maxFrameDeficitSeconds,
                $"L-3: active-phase GPU publication deficit stays below {maxFrameDeficitSeconds:F3}s");
            position.MaxSeconds.Should().BeLessThan(maxPositionStallSeconds,
                $"L-3: playback does not stall behind progressing LTC for {maxPositionStallSeconds:F3}s");

            int minimumHealthSamples = Math.Max(2,
                (int)Math.Floor(totalSeconds / healthIntervalSeconds * 0.8));
            health.Samples.Should().BeGreaterThanOrEqualTo(minimumHealthSamples,
                "L-3: independent process monitoring covers at least 80% of the full production day");
            health.UnavailableSamples.Should().Be(0, "L-3: process information remains readable");
            health.ExitedSamples.Should().Be(0, "L-3: the same app process remains alive");
            health.MaxUnresponsiveStreak.Should().BeLessThan(3,
                "L-3: the app is not unresponsive for three consecutive health samples");
            if (spoutRequired)
            {
                spoutUiOn.Should().BeTrue("L-3: Spout remains ON through post-show idle");
                spoutOffLogLines.Should().Be(0, "L-3: Spout is never switched OFF during the production day");
            }
            if (spoutReceiverSummary is not null)
            {
                spoutReceiverSummary.CompletedNormally.Should().BeTrue(
                    "L-3: the independent Spout receiver writes a complete successful summary");
                spoutReceiverSummary.Disconnects.Should().Be(0,
                    "L-3: the independent Spout receiver remains connected");
                spoutReceiverSummary.CounterResets.Should().Be(0,
                    "L-3: the Spout sender frame counter never moves backwards");
                spoutReceiverSummary.MaxGapMilliseconds.Should().BeLessThan(
                    maxSpoutReceiverGapMilliseconds,
                    $"L-3: independent Spout receive gaps stay below {maxSpoutReceiverGapMilliseconds:F0}ms");
            }
            if (maxPrivateGrowthMbPerHour is { } privateLimit)
                health.PrivateGrowthMbPerHour.Should().BeLessThanOrEqualTo(privateLimit,
                    $"L-3: Private Bytes growth stays below {privateLimit:F1} MiB/h");
            if (maxHandleGrowthPerHour is { } handleLimit)
                health.HandleGrowthPerHour.Should().BeLessThanOrEqualTo(handleLimit,
                    $"L-3: handle growth stays below {handleLimit:F1}/h");
        });

    private static L3PhaseResult L3RunActivePhase(
        Scenario scenario,
        string name,
        double durationSeconds,
        double lapSeconds,
        Stopwatch clock,
        List<LongRunInterval> exclusions,
        List<LongRunProgressSample> progress,
        List<double> errors)
    {
        double phaseStart = clock.Elapsed.TotalSeconds;
        double phaseEnd = phaseStart + durationSeconds;
        exclusions.Add(new LongRunInterval(phaseStart, Math.Min(phaseEnd, phaseStart + 20.0)));
        scenario.Journal.Write("l3-phase-start", details: new { name, kind = "active", durationSeconds });
        scenario.PlayRepeating(0.0, lapSeconds, durationSeconds + 5.0);

        int previousZone = int.MinValue;
        double stableAfter = phaseStart + 20.0;
        int samples = 0;
        int zoneChanges = 0;
        double nextCheckpoint = phaseStart + 600.0;
        while (clock.Elapsed.TotalSeconds < phaseEnd)
        {
            double elapsed = clock.Elapsed.TotalSeconds;
            scenario.App.Process.Refresh();
            if (scenario.App.Process.HasExited)
                throw new InvalidOperationException($"L-3: app exited during {name}");

            double ltc = scenario.LtcSeconds();
            double position = scenario.Position();
            int zone = L2ZoneOf(scenario.Tracks, ltc);
            samples++;
            if (previousZone == int.MinValue)
            {
                previousZone = zone;
            }
            else if (zone != previousZone)
            {
                zoneChanges++;
                exclusions.Add(new LongRunInterval(Math.Max(phaseStart, elapsed - 1.0),
                    Math.Min(phaseEnd, elapsed + L2SyncSettleTimeoutSeconds)));
                previousZone = zone;
                stableAfter = elapsed + L2SyncSettleTimeoutSeconds;
            }
            else if (zone >= 0 && elapsed >= stableAfter && double.IsFinite(position))
            {
                double error = Math.Abs(position - scenario.Tracks[zone].TimelineToMedia(ltc));
                if (double.IsFinite(error))
                {
                    errors.Add(error);
                    progress.Add(new LongRunProgressSample(elapsed, zone, ltc, position));
                }
            }

            if (elapsed >= nextCheckpoint)
            {
                scenario.Journal.Write("l3-checkpoint", details: new
                {
                    name,
                    atSeconds = Math.Round(elapsed, 1),
                    samples,
                    zoneChanges,
                });
                nextCheckpoint += 600.0;
            }
            Thread.Sleep(200);
        }

        // The audio device buffers slightly behind wall time. Keep the five-second
        // tail alive briefly before stopping so the audited phase is fully emitted.
        Thread.Sleep(2200);
        scenario.Signal.Stop();
        long sentFrames = scenario.Signal.SentFrames;
        long plannedFrames = scenario.Signal.PlannedFrames;
        scenario.Journal.Write("l3-phase-end", details: new
        {
            name,
            kind = "active",
            elapsedSeconds = Math.Round(clock.Elapsed.TotalSeconds - phaseStart, 1),
            samples,
            zoneChanges,
            sentFrames,
            plannedFrames,
        });
        return new L3PhaseResult(name, samples, zoneChanges, sentFrames, plannedFrames);
    }

    private static void L3RunIdlePhase(
        Scenario scenario,
        string name,
        double durationSeconds,
        Stopwatch clock,
        List<LongRunInterval> exclusions)
    {
        double phaseStart = clock.Elapsed.TotalSeconds;
        double phaseEnd = phaseStart + durationSeconds;
        exclusions.Add(new LongRunInterval(phaseStart, phaseEnd + 3.0));
        scenario.Signal.Stop();
        scenario.Journal.Write("l3-phase-start", details: new { name, kind = "idle", durationSeconds });
        double nextCheckpoint = phaseStart + 600.0;
        while (clock.Elapsed.TotalSeconds < phaseEnd)
        {
            scenario.App.Process.Refresh();
            if (scenario.App.Process.HasExited)
                throw new InvalidOperationException($"L-3: app exited during {name}");
            double remaining = phaseEnd - clock.Elapsed.TotalSeconds;
            Thread.Sleep(TimeSpan.FromSeconds(Math.Clamp(remaining, 0.1, 10.0)));
            if (clock.Elapsed.TotalSeconds >= nextCheckpoint)
            {
                scenario.Journal.Write("l3-checkpoint", details: new
                {
                    name,
                    atSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
                    responding = scenario.App.Process.Responding,
                });
                nextCheckpoint += 600.0;
            }
        }
        scenario.Journal.Write("l3-phase-end", details: new
        {
            name,
            kind = "idle",
            elapsedSeconds = Math.Round(clock.Elapsed.TotalSeconds - phaseStart, 1),
        });
    }

    private static double L3DurationFromEnvironment(string name, double fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            !double.IsFinite(parsed) || parsed <= 0)
            throw new InvalidOperationException($"{name} must be a positive number of seconds.");
        return parsed;
    }

    private readonly record struct L3PhaseResult(
        string Name, int Samples, int ZoneChanges, long SentFrames, long PlannedFrames);
}
