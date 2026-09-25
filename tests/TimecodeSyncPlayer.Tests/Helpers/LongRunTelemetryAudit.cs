using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TimecodeSyncPlayer.Tests.Helpers;

internal readonly record struct LongRunInterval(double StartSeconds, double EndSeconds)
{
    public bool Contains(double seconds) => seconds >= StartSeconds && seconds <= EndSeconds;

    public bool Overlaps(double startSeconds, double endSeconds) =>
        startSeconds <= EndSeconds && endSeconds >= StartSeconds;
}

/// <summary>
/// 長時間試験中の Playback perf 1 行。素材名やパスは保持しない。
/// GpuPublishedFrames は起動後の累積値、それ以外のフレーム数は SpanSeconds 内の値。
/// </summary>
internal readonly record struct LongRunPerfSample(
    double AtSeconds,
    double SpanSeconds,
    double ExpectedFps,
    double PlaybackRate,
    int FrameUpdates,
    long GpuPublishedFrames,
    long GstRingOutsideFrames,
    bool SpoutEnabled);

internal sealed record PlaybackContinuitySummary(
    int TotalSamples,
    int AuditedSamples,
    int ExcludedSamples,
    int ZeroUpdateSegments,
    int DeficitAtLeast100Ms,
    int DeficitAtLeast250Ms,
    int DeficitAtLeast500Ms,
    double MaxDeficitSeconds,
    double TotalDeficitSeconds,
    int TelemetryGaps,
    int GpuPublicationStalls,
    int GpuRateAuditedSegments,
    int GpuDeficitAtLeast100Ms,
    int GpuDeficitAtLeast250Ms,
    int GpuDeficitAtLeast500Ms,
    double MaxGpuDeficitSeconds,
    double TotalGpuDeficitSeconds,
    int PerfSpoutDisabledSamples);

/// <summary>
/// 2 秒単位のアプリ内計測から、期待フレーム数に対する不足とログ自体の欠落を調べる。
/// 不足秒は集計窓内の「不足フレーム数 / 期待fps」であり、連続フリーズ時間の断定値ではない。
/// </summary>
internal static class PlaybackContinuityAudit
{
    public static PlaybackContinuitySummary Summarize(
        IReadOnlyList<LongRunPerfSample> samples,
        IReadOnlyList<LongRunInterval>? exclusions = null,
        double telemetryGapSeconds = 5.0)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!double.IsFinite(telemetryGapSeconds) || telemetryGapSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(telemetryGapSeconds));

        IReadOnlyList<LongRunInterval> ignored = exclusions ?? [];
        LongRunPerfSample[] ordered = samples.OrderBy(sample => sample.AtSeconds).ToArray();
        var audited = new List<LongRunPerfSample>(ordered.Length);
        var deficits = new List<double>(ordered.Length);
        foreach (LongRunPerfSample sample in ordered)
        {
            double start = sample.AtSeconds - Math.Max(0.0, sample.SpanSeconds);
            if (ignored.Any(interval => interval.Overlaps(start, sample.AtSeconds)))
                continue;
            if (!double.IsFinite(sample.ExpectedFps) || sample.ExpectedFps <= 0 ||
                !double.IsFinite(sample.SpanSeconds) || sample.SpanSeconds <= 0 ||
                !double.IsFinite(sample.PlaybackRate) || sample.PlaybackRate <= 0)
                continue;

            audited.Add(sample);
            // playbackRate は再生位置の追い付き速度で、WPF/GPU の公開レートではない。
            // 速度補正中も表示更新はディスプレイ周期が上限なので、ここへ掛けると正常な
            // 5x catch-up を「窓全体の欠落」と誤判定する。
            double expected = sample.ExpectedFps * sample.SpanSeconds;
            double effectiveFps = sample.ExpectedFps;
            deficits.Add(Math.Max(0.0, expected - sample.FrameUpdates) / effectiveFps);
        }

        int telemetryGaps = 0;
        int gpuStalls = 0;
        var gpuDeficits = new List<double>(Math.Max(0, audited.Count - 1));
        for (int index = 1; index < audited.Count; index++)
        {
            LongRunPerfSample previous = audited[index - 1];
            LongRunPerfSample current = audited[index];
            double gap = current.AtSeconds - previous.AtSeconds;
            bool crossesExclusion = ignored.Any(interval =>
                interval.Overlaps(previous.AtSeconds, current.AtSeconds));
            if (gap > telemetryGapSeconds && !crossesExclusion)
                telemetryGaps++;
            if (current.GpuPublishedFrames <= previous.GpuPublishedFrames && !crossesExclusion)
                gpuStalls++;

            // gpuPublishedFrames は起動後の累積値なので、隣接ログ間の差を表示周期と
            // 比較する。ログ欠落と切替をまたぐ組は原因を分離できないため率監査から外す。
            if (crossesExclusion || !double.IsFinite(gap) || gap <= 0 || gap > telemetryGapSeconds ||
                !double.IsFinite(current.ExpectedFps) || current.ExpectedFps <= 0)
                continue;

            long publishedDelta = current.GpuPublishedFrames - previous.GpuPublishedFrames;
            if (publishedDelta < 0) // プロセス再起動などによる累積カウンターの巻き戻り
                continue;
            double expected = current.ExpectedFps * gap;
            gpuDeficits.Add(Math.Max(0.0, expected - publishedDelta) / current.ExpectedFps);
        }

        return new PlaybackContinuitySummary(
            ordered.Length,
            audited.Count,
            ordered.Length - audited.Count,
            audited.Count(sample => sample.FrameUpdates == 0),
            deficits.Count(value => value >= 0.100),
            deficits.Count(value => value >= 0.250),
            deficits.Count(value => value >= 0.500),
            deficits.Count == 0 ? 0.0 : deficits.Max(),
            deficits.Sum(),
            telemetryGaps,
            gpuStalls,
            gpuDeficits.Count,
            gpuDeficits.Count(value => value >= 0.100),
            gpuDeficits.Count(value => value >= 0.250),
            gpuDeficits.Count(value => value >= 0.500),
            gpuDeficits.Count == 0 ? 0.0 : gpuDeficits.Max(),
            gpuDeficits.Sum(),
            audited.Count(sample => !sample.SpoutEnabled));
    }
}

internal readonly record struct LongRunProgressSample(
    double AtSeconds,
    int Zone,
    double LtcSeconds,
    double PositionSeconds);

internal readonly record struct PositionStallSpan(
    double StartSeconds,
    double DurationSeconds,
    int Zone);

internal sealed record PositionContinuitySummary(
    IReadOnlyList<PositionStallSpan> Stalls,
    int AtLeast100Ms,
    int AtLeast250Ms,
    int AtLeast500Ms,
    double MaxSeconds);

/// <summary>
/// LTC が進んでいるのに位置ラベルがほぼ進まない隣接標本を連結し、再生位置の停止区間を作る。
/// トラック切替、Gap、読み取りが長く途切れた区間は判定しない。
/// </summary>
internal static class PositionContinuityAudit
{
    public static PositionContinuitySummary Summarize(
        IReadOnlyList<LongRunProgressSample> samples,
        double minimumAdvanceRatio = 0.20,
        double maximumPairSeconds = 1.0)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (!double.IsFinite(minimumAdvanceRatio) || minimumAdvanceRatio < 0 || minimumAdvanceRatio >= 1)
            throw new ArgumentOutOfRangeException(nameof(minimumAdvanceRatio));
        if (!double.IsFinite(maximumPairSeconds) || maximumPairSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPairSeconds));

        LongRunProgressSample[] ordered = samples.OrderBy(sample => sample.AtSeconds).ToArray();
        var stalls = new List<PositionStallSpan>();
        double? stallStart = null;
        double stallEnd = 0.0;
        int stallZone = -1;

        void CloseStall()
        {
            if (stallStart is not { } start)
                return;
            stalls.Add(new PositionStallSpan(start, Math.Max(0.0, stallEnd - start), stallZone));
            stallStart = null;
        }

        for (int index = 1; index < ordered.Length; index++)
        {
            LongRunProgressSample previous = ordered[index - 1];
            LongRunProgressSample current = ordered[index];
            double wallAdvance = current.AtSeconds - previous.AtSeconds;
            double ltcAdvance = current.LtcSeconds - previous.LtcSeconds;
            double positionAdvance = current.PositionSeconds - previous.PositionSeconds;
            bool measurable = previous.Zone >= 0 && current.Zone == previous.Zone &&
                double.IsFinite(wallAdvance) && wallAdvance > 0 && wallAdvance <= maximumPairSeconds &&
                double.IsFinite(ltcAdvance) && ltcAdvance > 0 &&
                double.IsFinite(positionAdvance);
            bool stalled = measurable && positionAdvance < ltcAdvance * minimumAdvanceRatio;
            if (stalled)
            {
                if (stallStart is null)
                {
                    stallStart = previous.AtSeconds;
                    stallZone = current.Zone;
                }
                stallEnd = current.AtSeconds;
            }
            else
            {
                CloseStall();
            }
        }
        CloseStall();

        return new PositionContinuitySummary(
            stalls,
            stalls.Count(stall => stall.DurationSeconds >= 0.100),
            stalls.Count(stall => stall.DurationSeconds >= 0.250),
            stalls.Count(stall => stall.DurationSeconds >= 0.500),
            stalls.Count == 0 ? 0.0 : stalls.Max(stall => stall.DurationSeconds));
    }
}

/// <summary>ネイティブ再生基盤のロード時間。入力行の path は意図的に保持しない。</summary>
internal sealed record NativeLoadTiming(
    int Sequence,
    int Attempt,
    string Profile,
    double TotalMilliseconds,
    double TeardownMilliseconds,
    double BuildMilliseconds,
    double SetStateMilliseconds,
    double PrerollMilliseconds,
    double FirstFrameMilliseconds,
    double AudioPrimeMilliseconds,
    double PauseMilliseconds,
    double SeekMilliseconds,
    double DurationMilliseconds,
    long Frames);

internal static partial class NativeLoadTimingParser
{
    [GeneratedRegex(
        @"load\.attempt path=.* paused=\d+ attempt=(\d+) profile=(\S+) result=(\S+) teardown_ms=([\d.]+) build_ms=([\d.]+) set_state_ms=([\d.]+) preroll_ms=([\d.]+) first_frame_ms=([\d.]+) audio_prime_ms=([\d.]+) pause_ms=([\d.]+) seek_ms=([\d.]+) duration_ms=([\d.]+) total_ms=([\d.]+) frames=(-?\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex AttemptPattern();

    [GeneratedRegex(
        @"load\.summary path=.* paused=\d+ total_ms=([\d.]+) attempt=(\d+) profile=(\S+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SummaryPattern();

    public static IReadOnlyList<NativeLoadTiming> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = new List<NativeLoadTiming>();
        PendingAttempt? pending = null;
        foreach (string line in lines)
        {
            Match attempt = AttemptPattern().Match(line);
            if (attempt.Success)
            {
                if (!string.Equals(attempt.Groups[3].Value, "ok", StringComparison.Ordinal))
                {
                    pending = null;
                    continue;
                }

                pending = new PendingAttempt(
                    Int(attempt, 1), attempt.Groups[2].Value,
                    Number(attempt, 4), Number(attempt, 5), Number(attempt, 6),
                    Number(attempt, 7), Number(attempt, 8), Number(attempt, 9),
                    Number(attempt, 10), Number(attempt, 11), Number(attempt, 12),
                    Number(attempt, 13), Long(attempt, 14));
                continue;
            }

            Match summary = SummaryPattern().Match(line);
            if (!summary.Success || pending is not { } found ||
                Int(summary, 2) != found.Attempt ||
                !string.Equals(summary.Groups[3].Value, found.Profile, StringComparison.Ordinal))
                continue;

            result.Add(new NativeLoadTiming(
                result.Count + 1, found.Attempt, found.Profile, Number(summary, 1),
                found.TeardownMilliseconds, found.BuildMilliseconds, found.SetStateMilliseconds,
                found.PrerollMilliseconds, found.FirstFrameMilliseconds, found.AudioPrimeMilliseconds,
                found.PauseMilliseconds, found.SeekMilliseconds, found.DurationMilliseconds, found.Frames));
            pending = null;
        }
        return result;
    }

    private static double Number(Match match, int group) =>
        double.Parse(match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);

    private static int Int(Match match, int group) =>
        int.Parse(match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);

    private static long Long(Match match, int group) =>
        long.Parse(match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);

    private sealed record PendingAttempt(
        int Attempt,
        string Profile,
        double TeardownMilliseconds,
        double BuildMilliseconds,
        double SetStateMilliseconds,
        double PrerollMilliseconds,
        double FirstFrameMilliseconds,
        double AudioPrimeMilliseconds,
        double PauseMilliseconds,
        double SeekMilliseconds,
        double DurationMilliseconds,
        double AttemptTotalMilliseconds,
        long Frames);
}

internal sealed record ProcessHealthSample(
    double AtSeconds,
    bool Available,
    bool HasExited,
    int? ExitCode,
    bool? Responding,
    long? PrivateMemoryBytes,
    long? WorkingSetBytes,
    int? HandleCount,
    int? ThreadCount,
    double? CpuPercent);

internal sealed record ProcessHealthSummary(
    int Samples,
    int UnavailableSamples,
    int ExitedSamples,
    int MaxUnresponsiveStreak,
    long PeakPrivateMemoryBytes,
    long PeakWorkingSetBytes,
    int PeakHandleCount,
    int PeakThreadCount,
    double PeakCpuPercent,
    bool TrendMeasurable,
    double PrivateGrowthMbPerHour,
    double WorkingSetGrowthMbPerHour,
    double HandleGrowthPerHour,
    double ThreadGrowthPerHour);

internal static class ProcessHealthAudit
{
    public static ProcessHealthSummary Summarize(
        IReadOnlyList<ProcessHealthSample> samples,
        double warmupSeconds = 0.0,
        double minimumTrendSeconds = 60.0)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ProcessHealthSample[] valid = samples
            .Where(sample => sample.Available && !sample.HasExited)
            .OrderBy(sample => sample.AtSeconds).ToArray();

        int streak = 0;
        int maxStreak = 0;
        foreach (ProcessHealthSample sample in samples.OrderBy(sample => sample.AtSeconds))
        {
            streak = sample.Responding == false ? streak + 1 : 0;
            maxStreak = Math.Max(maxStreak, streak);
        }

        ProcessHealthSample[] trend = valid.Where(sample => sample.AtSeconds >= warmupSeconds).ToArray();
        bool trendMeasurable = trend.Length >= 6 &&
            trend[^1].AtSeconds - trend[0].AtSeconds >= minimumTrendSeconds;
        double privateGrowth = 0.0;
        double workingGrowth = 0.0;
        double handleGrowth = 0.0;
        double threadGrowth = 0.0;
        if (trendMeasurable)
        {
            int width = Math.Max(3, trend.Length / 5);
            ProcessHealthSample[] first = trend.Take(width).ToArray();
            ProcessHealthSample[] last = trend.TakeLast(width).ToArray();
            double hours = (Median(last.Select(sample => sample.AtSeconds)) -
                            Median(first.Select(sample => sample.AtSeconds))) / 3600.0;
            if (hours > 0)
            {
                privateGrowth = BytesPerHour(first.Select(sample => sample.PrivateMemoryBytes),
                    last.Select(sample => sample.PrivateMemoryBytes), hours);
                workingGrowth = BytesPerHour(first.Select(sample => sample.WorkingSetBytes),
                    last.Select(sample => sample.WorkingSetBytes), hours);
                handleGrowth = PerHour(first.Select(sample => sample.HandleCount),
                    last.Select(sample => sample.HandleCount), hours);
                threadGrowth = PerHour(first.Select(sample => sample.ThreadCount),
                    last.Select(sample => sample.ThreadCount), hours);
            }
        }

        return new ProcessHealthSummary(
            samples.Count,
            samples.Count(sample => !sample.Available),
            samples.Count(sample => sample.HasExited),
            maxStreak,
            valid.Select(sample => sample.PrivateMemoryBytes ?? 0).DefaultIfEmpty().Max(),
            valid.Select(sample => sample.WorkingSetBytes ?? 0).DefaultIfEmpty().Max(),
            valid.Select(sample => sample.HandleCount ?? 0).DefaultIfEmpty().Max(),
            valid.Select(sample => sample.ThreadCount ?? 0).DefaultIfEmpty().Max(),
            valid.Select(sample => sample.CpuPercent ?? 0.0).DefaultIfEmpty().Max(),
            trendMeasurable,
            privateGrowth,
            workingGrowth,
            handleGrowth,
            threadGrowth);
    }

    private static double BytesPerHour(
        IEnumerable<long?> first, IEnumerable<long?> last, double hours) =>
        (Median(last.Where(value => value.HasValue).Select(value => (double)value!.Value)) -
         Median(first.Where(value => value.HasValue).Select(value => (double)value!.Value))) /
        (1024.0 * 1024.0) / hours;

    private static double PerHour(
        IEnumerable<int?> first, IEnumerable<int?> last, double hours) =>
        (Median(last.Where(value => value.HasValue).Select(value => (double)value!.Value)) -
         Median(first.Where(value => value.HasValue).Select(value => (double)value!.Value))) / hours;

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
            return 0.0;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }
}

/// <summary>
/// 対象プロセスを一定間隔で別スレッドから監視し、逐次 JSONL へ書く。
/// 出力はプロセス数値だけで、動画名・動画パスを含まない。
/// </summary>
internal sealed class ProcessHealthMonitor : IDisposable
{
    private readonly Process _process;
    private readonly TimeSpan _interval;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<ProcessHealthSample> _samples = [];
    private readonly StreamWriter _writer;
    private readonly Task _worker;
    private TimeSpan? _previousCpu;
    private double _previousAtSeconds;
    private bool _stopped;

    public ProcessHealthMonitor(Process process, TimeSpan interval, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));
        string fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _writer = new StreamWriter(
            new FileStream(fullPath, FileMode.Create, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete),
            new UTF8Encoding(false)) { AutoFlush = true };
        _process = process;
        _interval = interval;
        Capture();
        _worker = Task.Run(RunAsync);
    }

    public IReadOnlyList<ProcessHealthSample> Stop()
    {
        if (!_stopped)
        {
            _stopped = true;
            _cancellation.Cancel();
            try { _worker.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            Capture();
        }

        lock (_samples)
            return _samples.ToArray();
    }

    public void Dispose()
    {
        Stop();
        _writer.Dispose();
        _cancellation.Dispose();
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(_cancellation.Token).ConfigureAwait(false))
            Capture();
    }

    private void Capture()
    {
        double atSeconds = _clock.Elapsed.TotalSeconds;
        ProcessHealthSample sample;
        try
        {
            _process.Refresh();
            bool exited = _process.HasExited;
            int? exitCode = exited ? _process.ExitCode : null;
            bool? responding = exited ? null : _process.Responding;
            TimeSpan? cpu = exited ? null : _process.TotalProcessorTime;
            double? cpuPercent = null;
            if (cpu is { } currentCpu && _previousCpu is { } previousCpu)
            {
                double wallSeconds = atSeconds - _previousAtSeconds;
                if (wallSeconds > 0)
                {
                    cpuPercent = Math.Max(0.0, (currentCpu - previousCpu).TotalSeconds /
                        wallSeconds / Environment.ProcessorCount * 100.0);
                }
            }
            if (cpu is { } observedCpu)
            {
                _previousCpu = observedCpu;
                _previousAtSeconds = atSeconds;
            }

            sample = new ProcessHealthSample(
                atSeconds, true, exited, exitCode, responding,
                exited ? null : _process.PrivateMemorySize64,
                exited ? null : _process.WorkingSet64,
                exited ? null : _process.HandleCount,
                exited ? null : _process.Threads.Count,
                cpuPercent);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            sample = new ProcessHealthSample(
                atSeconds, false, false, null, null, null, null, null, null, null);
        }

        lock (_samples)
        {
            _samples.Add(sample);
            _writer.WriteLine(JsonSerializer.Serialize(sample, MonkeyJson.Options));
        }
    }
}
