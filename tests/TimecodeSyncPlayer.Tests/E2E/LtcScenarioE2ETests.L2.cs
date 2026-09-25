using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// L-2: Continue モードのプレイリストを何周も回す長時間試験。
///
/// <para>
/// LTC をプレイリストの先頭から最後のトラックの終わりまで流し、終わったら先頭へ戻して
/// 繰り返す。1 周ごとに「トラック切替」「ギャップの出入り」「周回の戻り（大きな逆方向の
/// 跳躍）」が一通り起きるので、切替の経路を何十〜何百回と踏む。長さは
/// TCS_L1_FOLLOW_SECONDS で指定する（L-1 と共通）。
/// </para>
///
/// <para>
/// 測るのは (1) 切替の回数と、切替ごとに絵が出るまでの時間、(2) ギャップの出入りの回数と
/// 黒になるまでの時間、(3) トラック内に居るときの追従誤差（10 分ごと）、
/// (4) ギャップ中の `Decode behind` の件数（期待 0）。
/// </para>
/// </summary>
public sealed partial class LtcScenarioE2ETests
{
    /// <summary>
    /// L-2: 切替の着地（誤差が許容内に入った時点）から、さらに誤差の集計を始めるまでの余裕。
    /// 着地そのものは <see cref="L2ProbeTransition"/> の段 2 が待つので、ここは短くてよい。
    /// </summary>
    private const double L2SettleAfterSwitchSeconds = 0.5;

    /// <summary>L-2: 切替後に絵（またはギャップの黒）が出るまで待つ上限。</summary>
    private const double L2TransitionTimeoutSeconds = 5.0;

    // 読み取りの前後がこの幅で計画上の境目に掛かる標本は「境目の標本」として誤差の集計から外す。
    private const double L2BoundaryGuardSeconds = 0.3;

    // 表示 LTC が計画 LTC からこれ以上離れた標本は、表示の化け・読み遅れとして誤差の計算に使わない。
    private const double L2DisplayMismatchSeconds = 0.5;

    /// <summary>
    /// L-2: 切替後、誤差が許容内に落ち着くまで待つ上限。実測では Continue の切替直後に
    /// 0.67〜1.14 秒ずれた状態から始まり、アプリがシークで詰める（1 回で収まらず 4 回
    /// 重ねた切替もあった）。シークが 1.4 秒かかる素材ではシークしても追い付けず、
    /// アプリは速度補正へ切り替える（`rate catch-up preferred`）。その追い付きは実測で
    /// 12.3 秒かかったので、上限はそれを含む長さに置く。
    /// </summary>
    private const double L2SyncSettleTimeoutSeconds = 20.0;

    /// <summary>
    /// L-2: 切替の見え方を 4 コマ残す回数。TCS_L2_CAPTURE_SWITCHES で指定する（既定 0）。
    /// 残す回は着地時間の測定が 1.6 秒遅れるので、診断のときだけ使う。
    /// </summary>
    private static int L2CapturedSwitches;

    private static int L2CaptureSwitchLimitFromEnvironment() =>
        int.TryParse(Environment.GetEnvironmentVariable("TCS_L2_CAPTURE_SWITCHES"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : 0;

    [SkippableFact(Timeout = 34_200_000)]
    public void L2_Continue_PlaylistSoak_KeepsSwitchingCleanly() => Run("L-2", continueMode: true, blackGap: true, scenario =>
    {
        double requestedSeconds = FollowSecondsFromEnvironment();
        // 1 周 = 先頭（タイムライン 0）から最後のトラックの終わりまで。ギャップも周回に含める。
        double lapSeconds = scenario.Tracks.Max(track => track.End);
        Skip.If(lapSeconds < 10.0, $"L-2: プレイリストが {lapSeconds:F1}s しかなく周回にならない");
        double totalSeconds = Math.Max(lapSeconds, requestedSeconds);

        scenario.SetSync(true);
        bool spoutRequired = L2BooleanFromEnvironment("TCS_L2_ENABLE_SPOUT");
        if (spoutRequired)
            scenario.SetSpout(true);
        double healthIntervalSeconds = L2PositiveDoubleFromEnvironment(
            "TCS_L2_HEALTH_SAMPLE_SECONDS", 10.0);
        double maxFrameDeficitSeconds = L2PositiveDoubleFromEnvironment(
            "TCS_L2_MAX_FRAME_DEFICIT_SECONDS", 0.5);
        double maxPositionStallSeconds = L2PositiveDoubleFromEnvironment(
            "TCS_L2_MAX_POSITION_STALL_SECONDS", 0.5);
        double maxSpoutReceiverGapMilliseconds = L2PositiveDoubleFromEnvironment(
            "TCS_L2_MAX_SPOUT_RECEIVER_GAP_MS", 500.0);
        double probeIntervalMilliseconds = L2PositiveDoubleFromEnvironment(
            "TCS_L2_PROBE_INTERVAL_MS", 250.0);
        double maxLoadMilliseconds = L2PositiveDoubleFromEnvironment(
            "TCS_L2_MAX_LOAD_MILLISECONDS", 5000.0);
        double? maxPrivateGrowthMbPerHour = L2OptionalPositiveDoubleFromEnvironment(
            "TCS_L2_MAX_PRIVATE_GROWTH_MB_PER_HOUR");
        double? maxHandleGrowthPerHour = L2OptionalPositiveDoubleFromEnvironment(
            "TCS_L2_MAX_HANDLE_GROWTH_PER_HOUR");
        scenario.Journal.Write("l2-plan", details: new
        {
            requestedSeconds,
            ltcFps = LtcFps,
            totalSeconds = Math.Round(totalSeconds, 3),
            lapSeconds = Math.Round(lapSeconds, 3),
            laps = Math.Round(totalSeconds / lapSeconds, 2),
            settleAfterSwitchSeconds = L2SettleAfterSwitchSeconds,
            spoutRequired,
            healthIntervalSeconds,
            maxFrameDeficitSeconds,
            maxPositionStallSeconds,
            maxSpoutReceiverGapMilliseconds,
            probeIntervalMilliseconds,
            maxLoadMilliseconds,
            maxPrivateGrowthMbPerHour,
            maxHandleGrowthPerHour,
            tracks = scenario.Tracks.Select(track => new
            {
                track.Symbol,
                start = Math.Round(track.Start, 3),
                end = Math.Round(track.End, 3),
                used = Math.Round(track.Used, 3),
            }),
        });

        long nativeLogOffset = scenario.NativeLogOffset();
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

        // 送出は 1 周ぶんを繰り返す。最後まで流し切ったかは終了時に検算する。
        DateTime startedAt = DateTime.Now;
        long auditOriginQpc = Stopwatch.GetTimestamp();
        scenario.Journal.Write("l2-audit-origin", details: new
        {
            startedAt,
            qpc = auditOriginQpc,
            qpcFrequency = Stopwatch.Frequency,
            externalSpoutReceiver = spoutReceiver is not null,
        });
        scenario.PlayRepeating(0.0, lapSeconds, totalSeconds + 5.0);
        var transitions = new List<L2Transition>();
        var errorSamples = new List<L2ErrorSample>();
        var progressSamples = new List<LongRunProgressSample>();
        var ltcReadMilliseconds = new List<double>();
        var positionReadMilliseconds = new List<double>();
        var probeGapMilliseconds = new List<double>();
        long previousProbeQpc = 0;
        int previousZone = int.MinValue;
        double lastTransitionElapsed = 0.0;
        // 計画 LTC（harness が流した値）と表示 LTC が大きく食い違った標本と、読み取りの途中で計画上の
        // トラックの境目をまたいだ標本は、誤差の集計から外して別に数える。
        int ltcDisplayMismatchSamples = 0;
        int boundarySamples = 0;
        var displayMinusPlanned = new List<double>();
        int samples = 0;
        int positionUnreadable = 0;

        while ((DateTime.Now - startedAt).TotalSeconds < totalSeconds)
        {
            long probeStartedQpc = Stopwatch.GetTimestamp();
            if (previousProbeQpc != 0)
                probeGapMilliseconds.Add(
                    (probeStartedQpc - previousProbeQpc) * 1000.0 / Stopwatch.Frequency);
            previousProbeQpc = probeStartedQpc;
            double elapsed = (DateTime.Now - startedAt).TotalSeconds;
            // L-1 と同じ読み取り（LTC を先に読み、読み取り時間の半分だけ進めて位置に合わせる）。
            // 遷移の判定には計画 LTC を使う。表示 LTC は誤差の計算と参考の記録だけに使う。
            double plannedBefore = scenario.Signal.PlannedLtcSeconds();
            DateTime ltcStartedAt = DateTime.Now;
            double ltcRaw = scenario.LtcSeconds();
            DateTime ltcEndedAt = DateTime.Now;
            double position = scenario.Position();
            DateTime positionEndedAt = DateTime.Now;
            double plannedAfter = scenario.Signal.PlannedLtcSeconds();
            double ltcReadMs = (ltcEndedAt - ltcStartedAt).TotalMilliseconds;
            double positionReadMs = (positionEndedAt - ltcEndedAt).TotalMilliseconds;
            ltcReadMilliseconds.Add(ltcReadMs);
            positionReadMilliseconds.Add(positionReadMs);
            double probeGapMs = probeGapMilliseconds.Count == 0 ? 0.0 : probeGapMilliseconds[^1];
            double probeOverrunMs = Math.Max(0.0, probeGapMs - probeIntervalMilliseconds);
            if (ltcReadMs >= 100.0 || positionReadMs >= 100.0 || probeOverrunMs >= 250.0)
                scenario.Journal.Write("l2-ui-probe-delay", details: new
                {
                    atSeconds = Math.Round(elapsed, 3),
                    ltcReadMs = Math.Round(ltcReadMs, 3),
                    positionReadMs = Math.Round(positionReadMs, 3),
                    probeGapMs = Math.Round(probeGapMs, 3),
                    probeOverrunMs = Math.Round(probeOverrunMs, 3),
                });
            double skewSeconds =
                ((positionEndedAt - ltcEndedAt).TotalSeconds + (ltcEndedAt - ltcStartedAt).TotalSeconds) / 2.0;
            double ltc = ltcRaw + skewSeconds;
            samples++;

            double planned = double.IsFinite(plannedBefore) && double.IsFinite(plannedAfter)
                ? L2Midpoint(plannedBefore, plannedAfter, lapSeconds) : double.NaN;
            if (!double.IsFinite(planned))
            {
                Thread.Sleep((int)Math.Clamp(Math.Round(probeIntervalMilliseconds), 10.0, 1000.0));
                continue;
            }

            int zone = L2ZoneOf(scenario.Tracks, planned);
            // 読み取りの前後で計画上の境目（±L2BoundaryGuardSeconds）をまたいだ標本は、位置の値が切替中の
            // 表示から来ることがある（候補 4 の M1 の終わりの 0.95 s は、切替の 0.06 s 後に読んだ位置だった）。
            bool boundarySample = L2ZoneOf(scenario.Tracks, L2Wrap(plannedBefore - L2BoundaryGuardSeconds, lapSeconds))
                != L2ZoneOf(scenario.Tracks, L2Wrap(plannedAfter + L2BoundaryGuardSeconds, lapSeconds));
            double displayDelta = double.IsFinite(ltc) ? L2WrappedDelta(ltc, planned, lapSeconds) : double.NaN;
            bool displayMismatch = !double.IsFinite(displayDelta) || Math.Abs(displayDelta) > L2DisplayMismatchSeconds;
            if (!displayMismatch)
                displayMinusPlanned.Add(displayDelta);
            if (displayMismatch && ltcDisplayMismatchSamples++ < 200)
                scenario.Journal.Write("l2-ltc-display-mismatch", details: new
                {
                    atSeconds = Math.Round(elapsed, 3),
                    plannedLtcSeconds = Math.Round(planned, 3),
                    displayLtcSeconds = double.IsFinite(ltc) ? Math.Round(ltc, 3) : (double?)null,
                    deltaSeconds = double.IsFinite(displayDelta) ? Math.Round(displayDelta, 3) : (double?)null,
                });

            if (previousZone == int.MinValue)
            {
                previousZone = zone;
                lastTransitionElapsed = elapsed;
            }
            else if (zone != previousZone)
            {
                // 遷移（計画 LTC で決める）。トラックへ入ったなら絵が出るまで、ギャップへ入ったなら黒になるまでを測る。
                L2Transition transition = L2ProbeTransition(scenario, elapsed, planned, previousZone, zone);
                transitions.Add(transition);
                scenario.Journal.Write("l2-switch", details: new
                {
                    atSeconds = Math.Round(transition.AtSeconds, 3),
                    lap = (int)(transition.AtSeconds / lapSeconds),
                    from = L2ZoneName(scenario.Tracks, transition.FromZone),
                    to = L2ZoneName(scenario.Tracks, transition.ToZone),
                    ltcSeconds = Math.Round(transition.LtcSeconds, 3),
                    settleMs = (int)Math.Round(transition.SettleSeconds * 1000.0),
                    attempts = transition.Attempts,
                    timedOut = transition.TimedOut,
                    wantsBlack = transition.WantsBlack,
                    syncSettleMs = (int)Math.Round(transition.SyncSettleSeconds * 1000.0),
                    syncTimedOut = transition.SyncTimedOut,
                    firstErrorSeconds = double.IsFinite(transition.FirstErrorSeconds)
                        ? Math.Round(transition.FirstErrorSeconds, 3) : (double?)null,
                    // 絵が出た瞬間の位置と、そのとき本来あるべき位置。
                    pictureAtPositionSeconds = double.IsFinite(transition.PictureAtPositionSeconds)
                        ? Math.Round(transition.PictureAtPositionSeconds, 3) : (double?)null,
                    pictureExpectedPositionSeconds = double.IsFinite(transition.PictureExpectedPositionSeconds)
                        ? Math.Round(transition.PictureExpectedPositionSeconds, 3) : (double?)null,
                    pictureBehindSeconds =
                        double.IsFinite(transition.PictureAtPositionSeconds)
                            && double.IsFinite(transition.PictureExpectedPositionSeconds)
                            ? Math.Round(transition.PictureExpectedPositionSeconds
                                - transition.PictureAtPositionSeconds, 3) : (double?)null,
                    captured = transition.Captured,
                });
                previousZone = zone;
                lastTransitionElapsed = (DateTime.Now - startedAt).TotalSeconds;
                continue;
            }

            if (zone >= 0 && elapsed - lastTransitionElapsed >= L2SettleAfterSwitchSeconds && boundarySample)
            {
                if (boundarySamples++ < 200)
                    scenario.Journal.Write("l2-boundary-sample", details: new
                    {
                        atSeconds = Math.Round(elapsed, 3),
                        track = L2ZoneName(scenario.Tracks, zone),
                        plannedBeforeSeconds = Math.Round(plannedBefore, 3),
                        plannedAfterSeconds = Math.Round(plannedAfter, 3),
                        displayLtcSeconds = double.IsFinite(ltc) ? Math.Round(ltc, 3) : (double?)null,
                        positionSeconds = double.IsFinite(position) ? Math.Round(position, 3) : (double?)null,
                    });
            }
            else if (zone >= 0 && elapsed - lastTransitionElapsed >= L2SettleAfterSwitchSeconds && displayMismatch)
            {
                // 表示 LTC が化けた・遅れた標本は、誤差の計算に使えない（数は ltcDisplayMismatchSamples）。
            }
            else if (zone >= 0 && elapsed - lastTransitionElapsed >= L2SettleAfterSwitchSeconds)
            {
                double error = double.IsFinite(position) && double.IsFinite(ltc)
                    ? Math.Abs(position - scenario.Tracks[zone].TimelineToMedia(ltc))
                    : double.NaN;
                if (double.IsFinite(error))
                {
                    errorSamples.Add(new L2ErrorSample(elapsed, zone, error));
                    progressSamples.Add(new LongRunProgressSample(elapsed, zone, ltc, position));
                    // 許容超えは 10 分バケットに埋もれさせず、1 件ずつ残す（いつ・どのトラックで・
                    // 直前の切替から何秒後かが分からないと、過渡なのか本物なのか切り分けられない）。
                    if (error > PositionToleranceSeconds)
                        scenario.Journal.Write("l2-error-outlier", details: new
                        {
                            atSeconds = Math.Round(elapsed, 3),
                            lap = (int)(elapsed / lapSeconds),
                            track = L2ZoneName(scenario.Tracks, zone),
                            errorSeconds = Math.Round(error, 3),
                            sinceTransitionSeconds = Math.Round(elapsed - lastTransitionElapsed, 3),
                            ltcSeconds = Math.Round(ltc, 3),
                            positionSeconds = Math.Round(position, 3),
                        });
                }
                else
                {
                    positionUnreadable++;
                }
            }

            Thread.Sleep((int)Math.Clamp(Math.Round(probeIntervalMilliseconds), 10.0, 1000.0));
        }

        Thread.Sleep(2200); // 最後の `Playback perf` 行を確定させる
        IReadOnlyList<ProcessHealthSample> healthSamples = healthMonitor.Stop();
        List<LongRunPerfSample> perf = scenario.LongRunPerfSince(startedAt).ToList();
        long sentFrames = scenario.Signal.SentFrames;
        long plannedFrames = scenario.Signal.PlannedFrames;
        IReadOnlyList<double> decodeBehindAt = scenario.DecodeBehindSecondsSince(startedAt);
        IReadOnlyList<NativeLoadTiming> nativeLoads = scenario.NativeLoadTimingsSince(nativeLogOffset);
        scenario.Signal.Stop();
        ExternalSpoutReceiverSummary? spoutReceiverSummary = spoutReceiver?.Stop();

        IReadOnlyList<LongRunInterval> exclusions = L2BuildContinuityExclusions(transitions, totalSeconds);
        PlaybackContinuitySummary playbackContinuity =
            PlaybackContinuityAudit.Summarize(perf, exclusions);
        PositionContinuitySummary positionContinuity =
            PositionContinuityAudit.Summarize(progressSamples);
        double resourceWarmupSeconds = totalSeconds >= 3600.0 ? 1800.0 : 0.0;
        ProcessHealthSummary processHealth = ProcessHealthAudit.Summarize(
            healthSamples, resourceWarmupSeconds, minimumTrendSeconds: 60.0);
        bool spoutUiOn = scenario.SpoutIsOn();
        int spoutOffLogLines = scenario.CountLogMatchesSince(@"Spout 出力: OFF", startedAt);

        L2Transition[] trackEntries = transitions.Where(transition => transition.ToZone >= 0).ToArray();
        // 起動時点で先頭トラックは既にロード済み。タイムライン先頭に Gap がある構成では、
        // LTC が先頭トラックへ入る遷移は記録されるがネイティブの再ロードは起きない。
        // 2 周目以降の先頭トラック進入は通常の切替ロードなので、最初の 1 回だけ外す。
        bool firstEntryWasPreloaded = trackEntries.Length > 0 && trackEntries[0].ToZone == 0 &&
            Math.Abs(trackEntries[0].AtSeconds - scenario.Tracks[0].Start) < 3.0;
        L2Transition[] loadEntries = firstEntryWasPreloaded ? trackEntries[1..] : trackEntries;
        for (int index = 0; index < nativeLoads.Count; index++)
        {
            NativeLoadTiming load = nativeLoads[index];
            string target = index < loadEntries.Length
                ? L2ZoneName(scenario.Tracks, loadEntries[index].ToZone)
                : $"switch-{index + 1}";
            scenario.Journal.Write("l2-load", details: new
            {
                load.Sequence,
                target,
                load.Attempt,
                load.Profile,
                totalMs = Math.Round(load.TotalMilliseconds, 1),
                firstFrameMs = Math.Round(load.FirstFrameMilliseconds, 1),
                prerollMs = Math.Round(load.PrerollMilliseconds, 1),
                buildMs = Math.Round(load.BuildMilliseconds, 1),
                teardownMs = Math.Round(load.TeardownMilliseconds, 1),
            });
        }

        scenario.Journal.Write("l2-frame-continuity", details: new
        {
            playbackContinuity.TotalSamples,
            playbackContinuity.AuditedSamples,
            playbackContinuity.ExcludedSamples,
            playbackContinuity.ZeroUpdateSegments,
            playbackContinuity.DeficitAtLeast100Ms,
            playbackContinuity.DeficitAtLeast250Ms,
            playbackContinuity.DeficitAtLeast500Ms,
            maxDeficitSeconds = Math.Round(playbackContinuity.MaxDeficitSeconds, 3),
            totalDeficitSeconds = Math.Round(playbackContinuity.TotalDeficitSeconds, 3),
            playbackContinuity.TelemetryGaps,
            playbackContinuity.GpuPublicationStalls,
            playbackContinuity.GpuRateAuditedSegments,
            playbackContinuity.GpuDeficitAtLeast100Ms,
            playbackContinuity.GpuDeficitAtLeast250Ms,
            playbackContinuity.GpuDeficitAtLeast500Ms,
            maxGpuDeficitSeconds = Math.Round(playbackContinuity.MaxGpuDeficitSeconds, 3),
            totalGpuDeficitSeconds = Math.Round(playbackContinuity.TotalGpuDeficitSeconds, 3),
            // v0.4.7 の GPU 経路は RecordRenderedFrame を呼ばないため、Playback perf の
            // spoutEnabled は実際に ON でも false のまま。既知の観測不能値としてだけ残す。
            playbackContinuity.PerfSpoutDisabledSamples,
            spoutUiOn,
            spoutOffLogLines,
        });
        scenario.Journal.Write("l2-position-continuity", details: new
        {
            samples = progressSamples.Count,
            stalls = positionContinuity.Stalls.Count,
            positionContinuity.AtLeast100Ms,
            positionContinuity.AtLeast250Ms,
            positionContinuity.AtLeast500Ms,
            maxSeconds = Math.Round(positionContinuity.MaxSeconds, 3),
            longest = positionContinuity.Stalls
                .OrderByDescending(stall => stall.DurationSeconds)
                .Take(20)
                .Select(stall => new
                {
                    atSeconds = Math.Round(stall.StartSeconds, 3),
                    durationSeconds = Math.Round(stall.DurationSeconds, 3),
                    track = L2ZoneName(scenario.Tracks, stall.Zone),
                }),
        });
        scenario.Journal.Write("l2-process-health", details: new
        {
            processHealth.Samples,
            processHealth.UnavailableSamples,
            processHealth.ExitedSamples,
            processHealth.MaxUnresponsiveStreak,
            peakPrivateMb = Math.Round(processHealth.PeakPrivateMemoryBytes / 1024.0 / 1024.0, 1),
            peakWorkingSetMb = Math.Round(processHealth.PeakWorkingSetBytes / 1024.0 / 1024.0, 1),
            processHealth.PeakHandleCount,
            processHealth.PeakThreadCount,
            peakCpuPercent = Math.Round(processHealth.PeakCpuPercent, 1),
            resourceWarmupSeconds,
            processHealth.TrendMeasurable,
            privateGrowthMbPerHour = Math.Round(processHealth.PrivateGrowthMbPerHour, 2),
            workingSetGrowthMbPerHour = Math.Round(processHealth.WorkingSetGrowthMbPerHour, 2),
            handleGrowthPerHour = Math.Round(processHealth.HandleGrowthPerHour, 2),
            threadGrowthPerHour = Math.Round(processHealth.ThreadGrowthPerHour, 2),
        });
        if (spoutReceiverSummary is not null)
        {
            scenario.Journal.Write("l2-spout-receiver", details: new
            {
                spoutReceiverSummary.SummaryFound,
                spoutReceiverSummary.CompletedNormally,
                spoutReceiverSummary.ExitCode,
                spoutReceiverSummary.TimedOut,
                spoutReceiverSummary.Errors,
                spoutReceiverSummary.ConnectedEver,
                spoutReceiverSummary.FrameCounterAvailable,
                spoutReceiverSummary.Polls,
                spoutReceiverSummary.ReceiveFailures,
                spoutReceiverSummary.UniqueFrames,
                spoutReceiverSummary.ObservedIntervals,
                spoutReceiverSummary.CounterJumps,
                spoutReceiverSummary.MissedSenderFrames,
                spoutReceiverSummary.CounterResets,
                spoutReceiverSummary.Disconnects,
                spoutReceiverSummary.MetadataChanges,
                spoutReceiverSummary.GapsAtLeast100Ms,
                spoutReceiverSummary.GapsAtLeast250Ms,
                spoutReceiverSummary.GapsAtLeast500Ms,
                maxGapMs = Math.Round(spoutReceiverSummary.MaxGapMilliseconds, 3),
                spoutReceiverSummary.ReceiveAtLeast10Ms,
                spoutReceiverSummary.ReceiveAtLeast50Ms,
                spoutReceiverSummary.ReceiveAtLeast100Ms,
                maxReceiveMs = Math.Round(spoutReceiverSummary.MaxReceiveMilliseconds, 3),
                spoutReceiverSummary.PixelSamples,
                spoutReceiverSummary.BlackPixelSamples,
                spoutReceiverSummary.UnchangedPixelSamples,
                spoutReceiverSummary.ContentChanges,
                spoutReceiverSummary.MaxUnchangedSampleStreak,
                maxUnchangedMs = Math.Round(spoutReceiverSummary.MaxUnchangedMilliseconds, 3),
                spoutReceiverSummary.NonBlackRunsAtLeast100Ms,
                spoutReceiverSummary.NonBlackRunsAtLeast250Ms,
                spoutReceiverSummary.NonBlackRunsAtLeast500Ms,
                maxNonBlackUnchangedMs = Math.Round(
                    spoutReceiverSummary.MaxNonBlackUnchangedMilliseconds, 3),
                spoutReceiverSummary.MaxNonBlackUnchangedQpc,
                spoutReceiverSummary.ReadbackAtLeast10Ms,
                spoutReceiverSummary.ReadbackAtLeast50Ms,
                spoutReceiverSummary.ReadbackAtLeast100Ms,
                maxGpuReadbackMs = Math.Round(spoutReceiverSummary.MaxGpuReadbackMilliseconds, 3),
                wallSeconds = Math.Round(spoutReceiverSummary.WallSeconds, 3),
                cpuSeconds = Math.Round(spoutReceiverSummary.CpuSeconds, 3),
            });
        }
        double[] sortedLtcReadMs = ltcReadMilliseconds.OrderBy(value => value).ToArray();
        double[] sortedPositionReadMs = positionReadMilliseconds.OrderBy(value => value).ToArray();
        double[] sortedProbeGapMs = probeGapMilliseconds.OrderBy(value => value).ToArray();
        scenario.Journal.Write("l2-ui-probe", details: new
        {
            samples = ltcReadMilliseconds.Count,
            intervalTargetMs = probeIntervalMilliseconds,
            ltcReadP99Ms = Math.Round(L2Percentile(sortedLtcReadMs, 0.99), 3),
            ltcReadMaxMs = Math.Round(sortedLtcReadMs.DefaultIfEmpty().Max(), 3),
            positionReadP99Ms = Math.Round(L2Percentile(sortedPositionReadMs, 0.99), 3),
            positionReadMaxMs = Math.Round(sortedPositionReadMs.DefaultIfEmpty().Max(), 3),
            probeGapP99Ms = Math.Round(L2Percentile(sortedProbeGapMs, 0.99), 3),
            probeGapMaxMs = Math.Round(sortedProbeGapMs.DefaultIfEmpty().Max(), 3),
            probeGaps100Ms = sortedProbeGapMs.Count(value => value >= 100.0),
            probeGaps250Ms = sortedProbeGapMs.Count(value => value >= 250.0),
            probeGaps500Ms = sortedProbeGapMs.Count(value => value >= 500.0),
            probeOverruns100Ms = sortedProbeGapMs.Count(
                value => value - probeIntervalMilliseconds >= 100.0),
            probeOverruns250Ms = sortedProbeGapMs.Count(
                value => value - probeIntervalMilliseconds >= 250.0),
            probeOverruns500Ms = sortedProbeGapMs.Count(
                value => value - probeIntervalMilliseconds >= 500.0),
        });

        // 10 分ごとの誤差（時間とともに悪くなるかを見る）。
        foreach (var bucket in errorSamples.GroupBy(sample => (int)(sample.AtSeconds / 600.0)).OrderBy(group => group.Key))
        {
            double[] sorted = bucket.Select(sample => sample.ErrorSeconds).OrderBy(value => value).ToArray();
            scenario.Journal.Write("l2-error-bucket", details: new
            {
                fromMinutes = bucket.Key * 10,
                samples = sorted.Length,
                medianSeconds = Math.Round(L2Percentile(sorted, 0.50), 4),
                p90Seconds = Math.Round(L2Percentile(sorted, 0.90), 4),
                p99Seconds = Math.Round(L2Percentile(sorted, 0.99), 4),
                maxSeconds = Math.Round(sorted.Length == 0 ? 0.0 : sorted[^1], 4),
                overToleranceSamples = sorted.Count(value => value > PositionToleranceSeconds),
            });
        }

        double[] allErrors = errorSamples.Select(sample => sample.ErrorSeconds).OrderBy(value => value).ToArray();
        double[] settleMs = transitions.Where(transition => !transition.WantsBlack)
            .Select(transition => transition.SettleSeconds * 1000.0).OrderBy(value => value).ToArray();
        double[] blackMs = transitions.Where(transition => transition.WantsBlack)
            .Select(transition => transition.SettleSeconds * 1000.0).OrderBy(value => value).ToArray();
        double[] syncMs = transitions.Where(transition => !transition.WantsBlack)
            .Select(transition => transition.SyncSettleSeconds * 1000.0).OrderBy(value => value).ToArray();
        double[] firstErrors = transitions.Where(transition => double.IsFinite(transition.FirstErrorSeconds))
            .Select(transition => transition.FirstErrorSeconds).OrderBy(value => value).ToArray();
        // Continue の切替は `Playlist track loaded index=` ではなく専用の行に出る
        // （loaded index= は起動時と参照採取のときだけ）。
        int switchLogLines = scenario.CountLogMatchesSince(@"Continue mode: switching to track", startedAt);
        int gapLogLines = scenario.CountLogMatchesSince(@"Continue mode: entered gap", startedAt);
        int syncSeekLogLines = scenario.CountLogMatchesSince(@"Continue mode: sync seek", startedAt);
        // シークで追い付けないと判断してアプリが速度補正へ切り替えた回数と、その所要。
        // シークが 1.4 秒かかる素材では `landing window closed without progress` のあと
        // `rate catch-up preferred` になり、追い付きに 12 秒級かかる。
        int rateCatchUpPreferred = scenario.CountLogMatchesSince(@"rate catch-up preferred", startedAt);
        int landingWindowClosed = scenario.CountLogMatchesSince(
            @"landing window closed without progress", startedAt);
        double[] rateCatchUpSeconds = scenario.RateCatchUpSecondsSince(startedAt).OrderBy(value => value).ToArray();
        int decodeBehindInGap = decodeBehindAt.Count(at => L2ZoneOfTransitions(transitions, at) < 0);

        scenario.Journal.Write("l2-summary", details: new
        {
            totalSeconds = Math.Round(totalSeconds, 3),
            lapSeconds = Math.Round(lapSeconds, 3),
            laps = Math.Round(totalSeconds / lapSeconds, 2),
            samples,
            positionUnreadable,
            // 遷移は計画 LTC で判定。境目の標本と表示の食い違いは誤差の集計から外した数。
            transitionSource = "planned-ltc",
            boundarySamples,
            ltcDisplayMismatchSamples,
            // 表示 LTC − 計画 LTC（食い違いを除く）。計画値の遅れの見積もり（OutputLatencySeconds）の検算用。
            displayMinusPlannedMedianSeconds = displayMinusPlanned.Count == 0 ? (double?)null
                : Math.Round(L2Percentile(displayMinusPlanned.OrderBy(value => value).ToArray(), 0.50), 3),
            displayMinusPlannedP90AbsSeconds = displayMinusPlanned.Count == 0 ? (double?)null
                : Math.Round(L2Percentile(displayMinusPlanned.Select(Math.Abs).OrderBy(value => value).ToArray(), 0.90), 3),
            ltcSentFrames = sentFrames,
            ltcPlannedFrames = plannedFrames,
            ltcRequiredFrames = (long)Math.Floor(totalSeconds * LtcFps),
            ltcShortfallSeconds = Math.Round((Math.Floor(totalSeconds * LtcFps) - sentFrames) / LtcFps, 2),
            transitions = transitions.Count,
            trackEntries = transitions.Count(transition => !transition.WantsBlack),
            gapEntries = transitions.Count(transition => transition.WantsBlack),
            switchLogLines,
            gapLogLines,
            syncSeekLogLines,
            landingWindowClosed,
            rateCatchUpPreferred,
            rateCatchUpCount = rateCatchUpSeconds.Length,
            rateCatchUpMedianSeconds = Math.Round(L2Percentile(rateCatchUpSeconds, 0.50), 2),
            rateCatchUpMaxSeconds = Math.Round(
                rateCatchUpSeconds.Length == 0 ? 0.0 : rateCatchUpSeconds[^1], 2),
            timedOutTransitions = transitions.Count(transition => transition.TimedOut),
            firstPictureMedianMs = (int)Math.Round(L2Percentile(settleMs, 0.50)),
            firstPictureP90Ms = (int)Math.Round(L2Percentile(settleMs, 0.90)),
            firstPictureMaxMs = (int)Math.Round(settleMs.Length == 0 ? 0.0 : settleMs[^1]),
            gapBlackMedianMs = (int)Math.Round(L2Percentile(blackMs, 0.50)),
            gapBlackMaxMs = (int)Math.Round(blackMs.Length == 0 ? 0.0 : blackMs[^1]),
            // 切替後、誤差が許容内に落ち着くまで（段 2）。絵が出る時間とは別の指標。
            syncSettleMedianMs = (int)Math.Round(L2Percentile(syncMs, 0.50)),
            syncSettleP90Ms = (int)Math.Round(L2Percentile(syncMs, 0.90)),
            syncSettleMaxMs = (int)Math.Round(syncMs.Length == 0 ? 0.0 : syncMs[^1]),
            syncTimedOutTransitions = transitions.Count(transition => transition.SyncTimedOut),
            firstErrorMedianSeconds = Math.Round(L2Percentile(firstErrors, 0.50), 3),
            firstErrorMaxSeconds = Math.Round(firstErrors.Length == 0 ? 0.0 : firstErrors[^1], 3),
            errorSamples = allErrors.Length,
            errorMedianSeconds = Math.Round(L2Percentile(allErrors, 0.50), 4),
            errorP90Seconds = Math.Round(L2Percentile(allErrors, 0.90), 4),
            errorP99Seconds = Math.Round(L2Percentile(allErrors, 0.99), 4),
            errorMaxSeconds = Math.Round(allErrors.Length == 0 ? 0.0 : allErrors[^1], 4),
            overToleranceSamples = allErrors.Count(value => value > PositionToleranceSeconds),
            meanFrameUpdates = perf.Count == 0 ? 0.0 : Math.Round(perf.Average(segment => (double)segment.FrameUpdates), 2),
            zeroUpdateSegments = perf.Count(segment => segment.FrameUpdates == 0),
            perfSegments = perf.Count,
            auditedPerfSegments = playbackContinuity.AuditedSamples,
            maxFrameDeficitSeconds = Math.Round(playbackContinuity.MaxDeficitSeconds, 3),
            maxGpuDeficitSeconds = Math.Round(playbackContinuity.MaxGpuDeficitSeconds, 3),
            gpuDeficits500Ms = playbackContinuity.GpuDeficitAtLeast500Ms,
            telemetryGaps = playbackContinuity.TelemetryGaps,
            gpuPublicationStalls = playbackContinuity.GpuPublicationStalls,
            spoutRequired,
            spoutUiOn,
            spoutOffLogLines,
            externalSpoutReceiver = spoutReceiverSummary is not null,
            spoutReceiverCompleted = spoutReceiverSummary?.CompletedNormally,
            spoutReceiverFrames = spoutReceiverSummary?.UniqueFrames,
            spoutReceiverMissedFrames = spoutReceiverSummary?.MissedSenderFrames,
            spoutReceiverGaps500Ms = spoutReceiverSummary?.GapsAtLeast500Ms,
            maxSpoutReceiverGapMs = spoutReceiverSummary is null
                ? (double?)null : Math.Round(spoutReceiverSummary.MaxGapMilliseconds, 3),
            positionStalls500Ms = positionContinuity.AtLeast500Ms,
            maxPositionStallSeconds = Math.Round(positionContinuity.MaxSeconds, 3),
            nativeLoads = nativeLoads.Count,
            maxNativeLoadMs = Math.Round(
                nativeLoads.Count == 0 ? 0.0 : nativeLoads.Max(load => load.TotalMilliseconds), 1),
            healthSamples = processHealth.Samples,
            peakPrivateMb = Math.Round(processHealth.PeakPrivateMemoryBytes / 1024.0 / 1024.0, 1),
            peakHandleCount = processHealth.PeakHandleCount,
            privateGrowthMbPerHour = Math.Round(processHealth.PrivateGrowthMbPerHour, 2),
            handleGrowthPerHour = Math.Round(processHealth.HandleGrowthPerHour, 2),
            decodeBehind = decodeBehindAt.Count,
            decodeBehindInGap,
        });

        // 送出が最後まで流れたことを、アプリの数値より先に確かめる。予定には終端の余裕を
        // 積んであるので、求めるのは「監査した totalSeconds ぶんが流れたか」。
        long requiredFrames = (long)Math.Floor(totalSeconds * LtcFps);
        sentFrames.Should().BeGreaterThanOrEqualTo(requiredFrames,
            $"L-2: LTC が監査の最後まで流れている（送出 {sentFrames} / 必要 {requiredFrames} / " +
            $"予定 {plannedFrames} フレーム）");
        transitions.Count.Should().BeGreaterThan(0, "L-2: 周回の中で切替が 1 回以上起きる");
        transitions.Count(transition => transition.TimedOut).Should().Be(0,
            $"L-2: すべての切替で {L2TransitionTimeoutSeconds:F0} 秒以内に絵（またはギャップの黒）が出る" +
            $"（切替 {transitions.Count} 回、時間切れ {transitions.Count(transition => transition.TimedOut)} 回）");
        transitions.Count(transition => transition.SyncTimedOut).Should().Be(0,
            $"L-2: すべての切替で {L2SyncSettleTimeoutSeconds:F0} 秒以内に誤差が ±{PositionToleranceSeconds} 秒へ落ち着く" +
            $"（切替 {syncMs.Length} 回、中央 {L2Percentile(syncMs, 0.50):F0}ms、最大 " +
            $"{(syncMs.Length == 0 ? 0.0 : syncMs[^1]):F0}ms）");
        allErrors.Length.Should().BeGreaterThan(0, "L-2: 誤差の標本が取れている");
        L2Percentile(allErrors, 0.99).Should().BeLessThanOrEqualTo(PositionToleranceSeconds,
            $"L-2: 切替から {L2SettleAfterSwitchSeconds:F1} 秒以降の誤差の 99% が ±{PositionToleranceSeconds} 秒以内" +
            $"（標本 {allErrors.Length}、中央 {L2Percentile(allErrors, 0.50):F3}s、最大 {allErrors[^1]:F3}s）");
        playbackContinuity.AuditedSamples.Should().BeGreaterThan(0,
            "L-2: 切替と Gap を除いた安定区間の Playback perf が記録されている");
        playbackContinuity.MaxDeficitSeconds.Should().BeLessThan(maxFrameDeficitSeconds,
            $"L-2: 安定区間のフレーム不足が {maxFrameDeficitSeconds:F3} 秒相当未満" +
            $"（100ms以上 {playbackContinuity.DeficitAtLeast100Ms}、250ms以上 " +
            $"{playbackContinuity.DeficitAtLeast250Ms}、最大 {playbackContinuity.MaxDeficitSeconds:F3}s）");
        playbackContinuity.TelemetryGaps.Should().Be(0,
            $"L-2: Playback perf の記録が5秒を超えて途切れない（{playbackContinuity.TelemetryGaps} 件）");
        playbackContinuity.GpuPublicationStalls.Should().Be(0,
            $"L-2: 安定区間でGPU公開フレーム累積値が停止しない（{playbackContinuity.GpuPublicationStalls} 件）");
        playbackContinuity.GpuRateAuditedSegments.Should().BeGreaterThan(0,
            "L-2: 安定区間のGPU公開レートを監査できる隣接標本がある");
        playbackContinuity.MaxGpuDeficitSeconds.Should().BeLessThan(maxFrameDeficitSeconds,
            $"L-2: 安定区間のGPU公開不足が {maxFrameDeficitSeconds:F3} 秒相当未満" +
            $"（100ms以上 {playbackContinuity.GpuDeficitAtLeast100Ms}、250ms以上 " +
            $"{playbackContinuity.GpuDeficitAtLeast250Ms}、最大 {playbackContinuity.MaxGpuDeficitSeconds:F3}s）");
        positionContinuity.MaxSeconds.Should().BeLessThan(maxPositionStallSeconds,
            $"L-2: LTCだけ進み再生位置が止まる区間が {maxPositionStallSeconds:F3} 秒未満" +
            $"（100ms以上 {positionContinuity.AtLeast100Ms}、250ms以上 " +
            $"{positionContinuity.AtLeast250Ms}、最大 {positionContinuity.MaxSeconds:F3}s）");
        nativeLoads.Should().NotBeEmpty("L-2: Continue の切替ロード内訳がネイティブログから取れている");
        nativeLoads.Count.Should().BeGreaterThanOrEqualTo(loadEntries.Length,
            $"L-2: 全切替ロードの時間が取れている（対象 {loadEntries.Length}、ロード {nativeLoads.Count}）");
        nativeLoads.Select(load => load.TotalMilliseconds).DefaultIfEmpty().Max()
            .Should().BeLessThan(maxLoadMilliseconds,
                $"L-2: 内部ロード時間がすべて {maxLoadMilliseconds:F0}ms 未満");
        int minimumHealthSamples = Math.Max(2,
            (int)Math.Floor(totalSeconds / healthIntervalSeconds * 0.8));
        processHealth.Samples.Should().BeGreaterThanOrEqualTo(minimumHealthSamples,
            $"L-2: 独立プロセス監視が全時間の80%以上を覆う（実測 {processHealth.Samples} / 最低 {minimumHealthSamples}）");
        processHealth.UnavailableSamples.Should().Be(0,
            "L-2: プロセス情報を読めない監視周期が無い");
        processHealth.ExitedSamples.Should().Be(0,
            "L-2: 監査中にアプリが終了しない");
        processHealth.MaxUnresponsiveStreak.Should().BeLessThan(3,
            $"L-2: {healthIntervalSeconds:F1}秒間隔で3回連続の無応答が無い" +
            $"（最大 {processHealth.MaxUnresponsiveStreak} 回）");
        if (spoutRequired)
        {
            spoutUiOn.Should().BeTrue("L-2: 監査終了時までSpoutボタンがONのまま");
            spoutOffLogLines.Should().Be(0,
                $"L-2: 監査中にSpout OFFへの切替ログが無い（{spoutOffLogLines} 件）");
        }
        if (spoutReceiverSummary is not null)
        {
            spoutReceiverSummary.CompletedNormally.Should().BeTrue(
                "L-2: 独立Spout受信監査がsummaryを残して正常終了する");
            spoutReceiverSummary.ObservedIntervals.Should().BeGreaterThan(0,
                "L-2: 独立受信側で連続する送信フレーム番号を観測できる");
            spoutReceiverSummary.Disconnects.Should().Be(0,
                $"L-2: 独立Spout受信が途中で切断されない（{spoutReceiverSummary.Disconnects} 件）");
            spoutReceiverSummary.CounterResets.Should().Be(0,
                $"L-2: Spout送信フレーム番号が巻き戻らない（{spoutReceiverSummary.CounterResets} 件）");
            spoutReceiverSummary.MaxGapMilliseconds.Should().BeLessThan(
                maxSpoutReceiverGapMilliseconds,
                $"L-2: 独立Spout受信の停止が {maxSpoutReceiverGapMilliseconds:F0}ms 未満" +
                $"（100ms以上 {spoutReceiverSummary.GapsAtLeast100Ms}、250ms以上 " +
                $"{spoutReceiverSummary.GapsAtLeast250Ms}、最大 {spoutReceiverSummary.MaxGapMilliseconds:F1}ms）");
        }
        if (maxPrivateGrowthMbPerHour is { } privateLimit)
        {
            processHealth.TrendMeasurable.Should().BeTrue("L-2: Private Bytes の傾きを判定できる長さと標本数がある");
            processHealth.PrivateGrowthMbPerHour.Should().BeLessThanOrEqualTo(privateLimit,
                $"L-2: 30分ウォームアップ後の Private Bytes 増加が {privateLimit:F1} MiB/h 以下");
        }
        if (maxHandleGrowthPerHour is { } handleLimit)
        {
            processHealth.TrendMeasurable.Should().BeTrue("L-2: ハンドル数の傾きを判定できる長さと標本数がある");
            processHealth.HandleGrowthPerHour.Should().BeLessThanOrEqualTo(handleLimit,
                $"L-2: 30分ウォームアップ後のハンドル増加が {handleLimit:F1}/h 以下");
        }
        decodeBehindInGap.Should().Be(0,
            $"L-2: ギャップ中に Decode behind が出ない（全体 {decodeBehindAt.Count} 件、うちギャップ中 {decodeBehindInGap} 件）");
    });

    /// <summary>
    /// L-2: 1 回の遷移。WantsBlack はギャップへ入った（黒を待った）遷移。
    /// SettleSeconds は「絵（またはギャップの黒）が出るまで」、SyncSettleSeconds は
    /// そのあと「誤差が許容内に落ち着くまで」。Continue の切替直後は 0.76〜1.14 秒ずれた
    /// 状態から始まり、アプリがシークで詰めるので、固定の待ち時間で切ると追い付き途中を
    /// 誤差として数えてしまう。
    /// </summary>
    private readonly record struct L2Transition(
        double AtSeconds, double LtcSeconds, int FromZone, int ToZone,
        double SettleSeconds, int Attempts, bool TimedOut, bool WantsBlack,
        double SyncSettleSeconds, bool SyncTimedOut, double FirstErrorSeconds,
        double PictureAtPositionSeconds, double PictureExpectedPositionSeconds, bool Captured,
        double CompletedAtSeconds);

    private readonly record struct L2ErrorSample(double AtSeconds, int Zone, double ErrorSeconds);

    /// <summary>LTC のタイムライン秒がどのトラックに入るか。どれにも入らなければ -1（ギャップ）。</summary>
    private static int L2ZoneOf(IReadOnlyList<TrackInfo> tracks, double ltcSeconds)
    {
        if (!double.IsFinite(ltcSeconds))
            return int.MinValue;
        for (int index = 0; index < tracks.Count; index++)
        {
            if (ltcSeconds >= tracks[index].Start && ltcSeconds < tracks[index].End)
                return index;
        }

        return -1;
    }

    /// <summary>表示 LTC が計画 LTC から L2DisplayMismatchSeconds 以内ならそのまま、離れていれば NaN。</summary>
    private static double L2UsableDisplayLtc(Scenario scenario, double displayLtc)
    {
        double planned = scenario.Signal.PlannedLtcSeconds();
        if (!double.IsFinite(displayLtc) || !double.IsFinite(planned))
            return displayLtc;
        double lapSeconds = scenario.Tracks.Max(track => track.End);
        return Math.Abs(L2WrappedDelta(displayLtc, planned, lapSeconds)) <= L2DisplayMismatchSeconds ? displayLtc : double.NaN;
    }

    /// <summary>周回の長さで 0〜lap に折り返す。</summary>
    private static double L2Wrap(double seconds, double lapSeconds) =>
        lapSeconds > 0 ? ((seconds % lapSeconds) + lapSeconds) % lapSeconds : seconds;

    /// <summary>a − b を周回の折り返しを考えて −lap/2〜+lap/2 に収める。</summary>
    private static double L2WrappedDelta(double a, double b, double lapSeconds)
    {
        double delta = a - b;
        if (lapSeconds <= 0)
            return delta;
        delta = L2Wrap(delta, lapSeconds);
        return delta > lapSeconds / 2.0 ? delta - lapSeconds : delta;
    }

    /// <summary>読み取りの前後の計画 LTC の中点（周回の折り返しをまたいでもよい）。</summary>
    private static double L2Midpoint(double before, double after, double lapSeconds) =>
        L2Wrap(before + L2WrappedDelta(after, before, lapSeconds) / 2.0, lapSeconds);

    private static string L2ZoneName(IReadOnlyList<TrackInfo> tracks, int zone) =>
        zone >= 0 && zone < tracks.Count ? tracks[zone].Symbol : "gap";

    /// <summary>遷移の記録から、ある時刻がギャップ中だったかを引く（-1 がギャップ）。</summary>
    private static int L2ZoneOfTransitions(IReadOnlyList<L2Transition> transitions, double atSeconds)
    {
        int zone = int.MinValue;
        foreach (L2Transition transition in transitions)
        {
            if (transition.AtSeconds > atSeconds)
                break;
            zone = transition.ToZone;
        }

        return zone;
    }

    /// <summary>
    /// 切替の直後を 2 段で測る。段 1 は「黒でない絵」（ギャップへ入ったなら「黒」）が出るまで。
    /// 段 2 はトラックへ入ったときだけで、誤差が連続 2 回許容内に入るまで。PNG は残さない
    /// （周回のたびに何十回も測るため）。
    /// </summary>
    private static L2Transition L2ProbeTransition(
        Scenario scenario, double atSeconds, double ltcSeconds, int fromZone, int toZone)
    {
        bool wantsBlack = toZone < 0;
        // ギャップ 0 の構成ではトラックが隣接するので、切替の前後どちらも黒ではない。
        // 「黒でない絵」では切替を検出できないため、位置ラベルが新トラックの頭へ
        // 移ったことで見る。
        bool adjacent = !wantsBlack && fromZone >= 0;
        double headSeconds = toZone >= 0 ? scenario.Tracks[toZone].MediaIn.TotalSeconds + 3.0 : 0.0;
        DateTime probeStartedAt = DateTime.Now;
        int attempts = 0;
        bool satisfied = false;
        var frames = new List<(double At, double Luminance, double BlackFraction, string Color)>();
        FrameSignature? previousSignature = null;
        double lastChangeSeconds = double.NaN;
        while ((DateTime.Now - probeStartedAt).TotalSeconds < L2TransitionTimeoutSeconds)
        {
            attempts++;
            if (adjacent)
            {
                double position = scenario.Position();
                if (double.IsFinite(position) && position <= headSeconds)
                {
                    satisfied = true;
                    break;
                }

                continue;
            }

            FrameSignature signature = scenario.Measure();
            double at = (DateTime.Now - probeStartedAt).TotalSeconds;
            if (previousSignature is { } previous &&
                signature.MeanPixelDifferenceTo(previous) > LtcScenarioFrameProbe.SameFrameTolerance)
                lastChangeSeconds = at;
            previousSignature = signature;
            frames.Add((at, signature.MeanLuminance, signature.BlackFraction,
                LtcScenarioFrameProbe.DescribeNearestKnownColor(signature)));
            if (signature.IsBlack == wantsBlack)
            {
                satisfied = true;
                break;
            }
        }

        if (!satisfied && !adjacent)
        {
            // 時間切れの中身を残す: 最初・途中・最後の数枚の絵と、撮った絵が最後に変わった時刻
            // （プレビューが更新されていたかの目安）。最後の絵は PNG で報告フォルダーへ（素材の絵が
            // 写るので、報告フォルダーはリポジトリの外に置くこと）。
            int n = frames.Count;
            var picks = frames.Take(3)
                .Concat(frames.Skip(Math.Max(0, n / 2 - 1)).Take(3))
                .Concat(frames.Skip(Math.Max(0, n - 3)))
                .Select(f => new { atSeconds = Math.Round(f.At, 3), meanLuminance = Math.Round(f.Luminance, 1),
                    blackFraction = Math.Round(f.BlackFraction, 4), nearestKnownColor = f.Color })
                .ToArray();
            int index = ++L2TimeoutCaptures;
            FrameSignature last = scenario.Capture($"l2-timeout{index}-last");
            scenario.Journal.Write("l2-transition-timeout-frames", details: new
            {
                timeoutIndex = index,
                atSeconds = Math.Round(atSeconds, 3),
                from = L2ZoneName(scenario.Tracks, fromZone),
                to = L2ZoneName(scenario.Tracks, toZone),
                wantsBlack,
                attempts = n,
                contentChanges = frames.Count == 0 ? 0 : CountChanges(frames),
                lastChangeSeconds = double.IsFinite(lastChangeSeconds) ? Math.Round(lastChangeSeconds, 3) : (double?)null,
                lastImage = $"l2-timeout{index}-last.png",
                lastIsBlack = last.IsBlack,
                frames = picks,
            });
        }

        double pictureSeconds = (DateTime.Now - probeStartedAt).TotalSeconds;
        // 絵が出た瞬間に「どの位置の絵か」を読む。切替直後のずれが
        // 「新しいトラックの手前の絵が出てから飛ぶ」のか「前の絵が残っている」のかは、
        // この位置が新トラックの頭（MediaIn 付近）かどうかで分かれる。
        double pictureAtPosition = double.NaN;
        double pictureExpectedPosition = double.NaN;
        if (!wantsBlack)
        {
            // 絵が出た直後は位置ラベルがまだ空のことがある（実測で 30 回中 30 回読めなかった）。
            // 読めるまで短く粘る。粘った時間は下の着地測定には入らない。
            DateTime readDeadline = DateTime.Now.AddSeconds(0.5);
            while (true)
            {
                double ltcNow = L2UsableDisplayLtc(scenario, scenario.LtcSeconds());
                pictureAtPosition = scenario.Position();
                if (double.IsFinite(pictureAtPosition) && double.IsFinite(ltcNow))
                {
                    pictureExpectedPosition = scenario.Tracks[toZone].TimelineToMedia(ltcNow);
                    break;
                }

                if (DateTime.Now >= readDeadline)
                    break;
            }
        }

        bool captured = false;
        if (!wantsBlack && L2CapturedSwitches < L2CaptureSwitchLimitFromEnvironment())
        {
            // 見え方そのものを残す。切替から 0.4 秒おきに 4 枚、位置ラベルつきで。
            captured = true;
            int index = ++L2CapturedSwitches;
            for (int frame = 0; frame < 4; frame++)
            {
                double ltcAt = scenario.LtcSeconds();
                double positionAt = scenario.Position();
                FrameSignature signature = scenario.Capture($"l2-switch{index}-{frame}");
                scenario.Journal.Write("l2-switch-frame", details: new
                {
                    switchIndex = index,
                    frame,
                    sinceSwitchSeconds = Math.Round((DateTime.Now - probeStartedAt).TotalSeconds, 3),
                    track = scenario.Tracks[toZone].Symbol,
                    ltcSeconds = Math.Round(ltcAt, 3),
                    positionSeconds = Math.Round(positionAt, 3),
                    expectedPositionSeconds = double.IsFinite(ltcAt)
                        ? Math.Round(scenario.Tracks[toZone].TimelineToMedia(ltcAt), 3) : (double?)null,
                    isBlack = signature.IsBlack,
                    meanLuminance = Math.Round(signature.MeanLuminance, 1),
                });
                Thread.Sleep(400);
            }
        }

        double syncSettleSeconds = 0.0;
        bool syncTimedOut = false;
        double firstError = double.NaN;
        if (!wantsBlack)
        {
            // 位置ラベルはシーク直後に古い値を返すことがあり、1〜2 回の一致では
            // 追い付く前に「着地した」と読めてしまう（v0.4.7 の 1 周で、内部は 12.9 秒の
            // 速度補正をしているのに 235ms で着地と読んだ回があった）。少し待ってから、
            // 連続 GateStableSamples 回そろったときだけ着地とみなす。
            Thread.Sleep(300);
            DateTime syncStartedAt = DateTime.Now;
            int stable = 0;
            while (true)
            {
                // 表示 LTC が計画値から離れている（化け・読み遅れ）ときは、この標本を誤差に使わない。
                double ltc = L2UsableDisplayLtc(scenario, scenario.LtcSeconds());
                double position = scenario.Position();
                double error = double.IsFinite(ltc) && double.IsFinite(position)
                    ? Math.Abs(position - scenario.Tracks[toZone].TimelineToMedia(ltc))
                    : double.NaN;
                if (!double.IsFinite(firstError) && double.IsFinite(error))
                    firstError = error;
                if (double.IsFinite(error) && error <= PositionToleranceSeconds)
                {
                    if (++stable >= GateStableSamples)
                        break;
                }
                else
                {
                    stable = 0;
                }

                if ((DateTime.Now - syncStartedAt).TotalSeconds >= L2SyncSettleTimeoutSeconds)
                {
                    syncTimedOut = true;
                    break;
                }
            }

            syncSettleSeconds = (DateTime.Now - syncStartedAt).TotalSeconds;
        }

        double completedAtSeconds = atSeconds + (DateTime.Now - probeStartedAt).TotalSeconds;
        return new L2Transition(atSeconds, ltcSeconds, fromZone, toZone,
            pictureSeconds, attempts, !satisfied, wantsBlack,
            syncSettleSeconds, syncTimedOut, firstError,
            pictureAtPosition, pictureExpectedPosition, captured, completedAtSeconds);
    }

    private static IReadOnlyList<LongRunInterval> L2BuildContinuityExclusions(
        IReadOnlyList<L2Transition> transitions, double totalSeconds)
    {
        var result = new List<LongRunInterval>();
        for (int index = 0; index < transitions.Count; index++)
        {
            L2Transition transition = transitions[index];
            double end = Math.Min(totalSeconds,
                transition.CompletedAtSeconds + L2SettleAfterSwitchSeconds);
            result.Add(new LongRunInterval(Math.Max(0.0, transition.AtSeconds), end));
            if (!transition.WantsBlack)
                continue;

            double gapEnd = index + 1 < transitions.Count
                ? transitions[index + 1].AtSeconds
                : totalSeconds;
            result.Add(new LongRunInterval(
                Math.Max(0.0, transition.AtSeconds), Math.Min(totalSeconds, gapEnd)));
        }
        return result;
    }

    private static bool L2BooleanFromEnvironment(string variable) =>
        string.Equals(Environment.GetEnvironmentVariable(variable), "1", StringComparison.Ordinal);

    private static double L2PositiveDoubleFromEnvironment(string variable, double fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(variable);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
               double.IsFinite(parsed) && parsed > 0.0
            ? parsed
            : fallback;
    }

    private static double? L2OptionalPositiveDoubleFromEnvironment(string variable)
    {
        string? raw = Environment.GetEnvironmentVariable(variable);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
               double.IsFinite(parsed) && parsed > 0.0
            ? parsed
            : null;
    }

    private static int L2TimeoutCaptures;

    // 連続する標本で輝度が 0.5 以上動いた回数（プレビューの絵が変わっていたかの粗い目安）。
    private static int CountChanges(List<(double At, double Luminance, double BlackFraction, string Color)> frames)
    {
        int changes = 0;
        for (int i = 1; i < frames.Count; i++)
            if (Math.Abs(frames[i].Luminance - frames[i - 1].Luminance) > 0.5) changes++;
        return changes;
    }

    private static double L2Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 0)
            return 0.0;
        int index = (int)Math.Round((sorted.Length - 1) * fraction);
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private sealed partial class Scenario
    {
        public void SetSpout(bool enabled)
        {
            var button = App.Button("BtnSpout");
            WaitUntil(() => button.IsEnabled, 10, "Spout ボタンの有効化");
            bool current = App.Text("BtnSpout").Contains("ON", StringComparison.OrdinalIgnoreCase);
            if (current != enabled)
                button.Invoke();
            WaitUntil(
                () => App.Text("BtnSpout").Contains(enabled ? "ON" : "OFF",
                    StringComparison.OrdinalIgnoreCase),
                10, enabled ? "Spout ON" : "Spout OFF");
            Journal.Write("l2-spout", details: new { enabled });
        }

        public bool SpoutIsOn() =>
            App.Text("BtnSpout").Contains("ON", StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<LongRunPerfSample> LongRunPerfSince(DateTime sinceLocal)
        {
            var samples = new List<LongRunPerfSample>();
            foreach (string line in RunLogLinesSince(sinceLocal))
            {
                if (!line.Contains("Playback perf", StringComparison.Ordinal))
                    continue;
                Match timestamp = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)");
                if (!timestamp.Success ||
                    !DateTime.TryParse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime at) ||
                    !L2TryLogDouble(line, "elapsed", "s", out double elapsed) ||
                    !L2TryLogDouble(line, "expectedFps", "", out double expectedFps) ||
                    !L2TryLogDouble(line, "playbackRate", "", out double playbackRate) ||
                    !L2TryLogLong(line, "frameUpdates", out long frameUpdates) ||
                    !L2TryLogLong(line, "gpuPublishedFrames", out long gpuPublishedFrames) ||
                    !L2TryLogLong(line, "gstRingOutsideFrames", out long gstRingOutsideFrames))
                    continue;

                Match spout = Regex.Match(line, @"\bspoutEnabled=(true|false)\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!spout.Success)
                    continue;
                samples.Add(new LongRunPerfSample(
                    (at - sinceLocal).TotalSeconds,
                    elapsed,
                    expectedFps,
                    playbackRate,
                    checked((int)frameUpdates),
                    gpuPublishedFrames,
                    gstRingOutsideFrames,
                    bool.Parse(spout.Groups[1].Value)));
            }

            return samples;
        }

        public long NativeLogOffset()
        {
            string path = Path.Combine(ReportDir, "tcs-gst-raw.log");
            if (!File.Exists(path))
                return 0;
            return new FileInfo(path).Length;
        }

        public IReadOnlyList<NativeLoadTiming> NativeLoadTimingsSince(long offset)
        {
            string path = Path.Combine(ReportDir, "tcs-gst-raw.log");
            if (!File.Exists(path))
                return [];

            string text;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(Math.Clamp(offset, 0, stream.Length), SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            return NativeLoadTimingParser.Parse(text.Split('\n'));
        }

        private static bool L2TryLogDouble(
            string line, string name, string suffix, out double value)
        {
            value = 0.0;
            Match match = Regex.Match(line,
                $@"\b{Regex.Escape(name)}=([\d.]+){Regex.Escape(suffix)}");
            return match.Success && double.TryParse(match.Groups[1].Value,
                NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool L2TryLogLong(string line, string name, out long value)
        {
            value = 0;
            Match match = Regex.Match(line, $@"\b{Regex.Escape(name)}=(-?\d+)\b");
            return match.Success && long.TryParse(match.Groups[1].Value,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// L-2: 監査開始からの `rate catch-up settled ... elapsedSeconds=N` の所要秒。
        /// シークで追い付けず速度補正で詰めた回の、追い付きにかかった時間。
        /// </summary>
        public IReadOnlyList<double> RateCatchUpSecondsSince(DateTime sinceLocal)
        {
            var found = new List<double>();
            foreach (string line in RunLogLinesSince(sinceLocal))
            {
                Match match = Regex.Match(line, @"rate catch-up settled .*elapsedSeconds=([\d.]+)");
                if (match.Success && double.TryParse(match.Groups[1].Value,
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                    found.Add(seconds);
            }

            return found;
        }

        /// <summary>
        /// L-2: 監査開始からの `Decode behind` の発生時刻（監査開始からの経過秒）。
        /// ギャップ中に出たかを後から突き合わせるために時刻ごと拾う。
        /// </summary>
        public IReadOnlyList<double> DecodeBehindSecondsSince(DateTime sinceLocal)
        {
            var found = new List<double>();
            foreach (string line in RunLogLinesSince(sinceLocal))
            {
                if (!line.Contains("Decode behind", StringComparison.Ordinal)) continue;
                Match timestamp = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)");
                if (!timestamp.Success) continue;
                if (!DateTime.TryParse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateTime at)) continue;
                found.Add((at - sinceLocal).TotalSeconds);
            }

            return found;
        }
    }
}
