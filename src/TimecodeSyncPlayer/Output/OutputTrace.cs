using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Serilog;

namespace TimecodeSyncPlayer.Output;

internal sealed record OutputTraceEvent(string Stage, string Worker, long Qpc, long ScheduledQpc = 0, long ImageId = 0, long GeneratedQpc = 0, string? Detail = null, long Value = 0, long DeadlineQpc = 0, long PtsNs = 0);

/// <summary>
/// 出力トレース（試作 GpuOutputProbe の manifest.json / events.jsonl / summary.json と同スキーマ）。
/// 環境変数 TIMECODE_SYNC_PLAYER_OUTPUT_TRACE にディレクトリを指定したときだけ有効。
/// イベント上限は TIMECODE_SYNC_PLAYER_OUTPUT_TRACE_CAPACITY で変更できる（既定 1,000,000、不正値は既定）。
/// 試作と同じくイベントはメモリに溜め、停止時にまとめて書く（強制終了時は残らない）。
/// メモリの目安: 1,000,000 件で約 170MB、60 分 60Hz 相当（約 730 万件）で約 1.2GB（実測）。
/// 上限到達時は最初の 1 件で警告を 1 回出し、summary.json の droppedEvents / capacity で確認できる。
/// </summary>
internal sealed class OutputTrace
{
    public const string EnvironmentVariable = "TIMECODE_SYNC_PLAYER_OUTPUT_TRACE";
    public const string CapacityEnvironmentVariable = "TIMECODE_SYNC_PLAYER_OUTPUT_TRACE_CAPACITY";
    public const int DefaultCapacity = 1_000_000;
    private readonly ConcurrentQueue<OutputTraceEvent> events = new();
    private readonly int capacity;
    private int count, dropped, dropWarned;
    public long OriginQpc { get; set; }
    public string? Directory { get; }
    public bool IsEnabled => Directory != null;
    /// <summary>イベント上限（環境変数で変更可、既定 1,000,000）。</summary>
    public int Capacity => capacity;
    /// <summary>上限超過で破棄したイベント数（summary.json の droppedEvents と同じ）。</summary>
    internal int Dropped => Volatile.Read(ref dropped);
    /// <summary>記録したイベント数（破棄を除く）。</summary>
    internal int Recorded => Volatile.Read(ref count) - Dropped;

    /// <summary>テスト用: 現在までに記録したイベントのコピー（記録順、破棄分は含まない）。</summary>
    internal OutputTraceEvent[] Snapshot() => events.ToArray();

    internal static OutputTrace Disabled { get; } = new();

    /// <summary>
    /// 計測専用イベント（seek.decide / seek.issue / seek.return / mpv.frame）の共有参照。
    /// GPU 出力を開始するときに MainWindow が設定する。既定は無効で、記録側は
    /// IsEnabled を確認してから呼ぶ（無効時にコストを増やさない）。
    /// </summary>
    public static OutputTrace Current { get; set; } = Disabled;

    private OutputTrace() { capacity = DefaultCapacity; }

    internal OutputTrace(string directory, int capacity)
    {
        Directory = directory;
        this.capacity = capacity > 0 ? capacity : DefaultCapacity;
    }

    /// <summary>環境変数値を解釈する。未設定・不正・0 以下は既定値。</summary>
    internal static int ParseCapacity(string? value)
        => int.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : DefaultCapacity;

    public static OutputTrace Create(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return Disabled;
        try
        {
            string full = Path.GetFullPath(directory);
            System.IO.Directory.CreateDirectory(full);
            return new OutputTrace(full, ParseCapacity(Environment.GetEnvironmentVariable(CapacityEnvironmentVariable)));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "出力トレースのディレクトリを作成できません: {Directory}", directory);
            return Disabled;
        }
    }

    public void Add(string stage, string worker, long scheduled = 0, ImageStamp stamp = default, string? detail = null, long value = 0)
        => Record(new(stage, worker, Stopwatch.GetTimestamp(), scheduled, stamp.Id, stamp.GeneratedQpc, detail, value));

    public void Record(OutputTraceEvent item)
    {
        if (!IsEnabled) return;
        if (Interlocked.Increment(ref count) <= capacity) events.Enqueue(item);
        else
        {
            Interlocked.Increment(ref dropped);
            if (Interlocked.Exchange(ref dropWarned, 1) == 0)
                Log.Warning("出力トレースが上限 {Capacity} に達しました。以降のイベントは記録されません", capacity);
        }
    }

    public void Save(OutputTraceRunSummary run, LatestPool pool, ScanoutTracker? scanout, TimecodeSyncPlayer.Contracts.SourceDiagnostics? sourceDiagnostics = null)
    {
        if (!IsEnabled) return;
        try
        {
            var all = events.OrderBy(e => e.Qpc).ToArray();
            double end = all.Length == 0 ? 0 : (all[^1].Qpc - OriginQpc) / (double)Stopwatch.Frequency;
            string output = run.SpoutEnabled && run.DisplayAttached ? "both" : run.SpoutEnabled ? "spout" : "fullscreen";
            bool vblank = all.Any(e => e.Stage.StartsWith("present.", StringComparison.Ordinal) || e.Stage.StartsWith("display.vblank.", StringComparison.Ordinal));
            // source.acquire は契約ソース経路（mpv スナップショットアップロード）を示す。
            string sourceKind = all.Any(e => e.Stage == "source.acquire") ? "contract-fake" : "pattern";
            var options = new
            {
                mode = "split",
                output,
                width = run.CanvasWidth,
                height = run.CanvasHeight,
                fps = 60.0,
                seconds = end,
                warmup = 0.0,
                sender = run.SenderName,
                monitorIndex = 0,
                windowed = false,
                mutexWaitMs = MutexWaitPolicy.MaxWaitMs,
                sendPhaseMs = 4.0,
                presentWaitMs = 0,
                displayPacing = vblank ? "vblank" : "tick",
                copyRetry = "off",
                sourceSync = "fence",
                presentMarginMs = run.PresentMarginMs,
                composeAlign = vblank ? "vblank" : "off",
                composeLeadMs = run.ComposeLeadMs,
                source = sourceKind,
                sourceWidth = run.CanvasWidth,
                sourceHeight = run.CanvasHeight
            };
            long origin = OriginQpc;
            var manifest = new
            {
                schemaVersion = 1,
                options,
                qpcFrequency = Stopwatch.Frequency,
                originQpc = origin,
                highResolutionTimer = run.VblankTimerHighResolution,
                loopTimerHighResolution = new { gpu = run.GpuLoopTimerHighResolution, spout = run.SpoutLoopTimerHighResolution },
                alignSlewMs = ComposeAlignGate.SlewMs,
                senderName = run.SenderName,
                canvas = new { width = run.CanvasWidth, height = run.CanvasHeight },
                limitation = "本体統合の出力層。GPU 生成の黒キャンバスまたはテストカードのみで、mpv 映像は含まない（段階 1）。"
            };
            WriteNew(Path.Combine(Directory!, "manifest.json"), manifest);

            using (var writer = new StreamWriter(new FileStream(Path.Combine(Directory!, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)))
                foreach (var e in all) writer.WriteLine(JsonSerializer.Serialize(e, Json));

            var window = all.Where(e => e.Qpc >= origin && e.Qpc < origin + (long)(end * Stopwatch.Frequency)).ToArray();
            object Stats(double[] a) => new { count = a.Length, mean = a.Length == 0 ? (double?)null : a.Average(), p95 = Percentile(a, .95), p99 = Percentile(a, .99), max = a.Length == 0 ? (double?)null : a.Max() };
            var metrics = new Dictionary<string, object>();
            foreach (string stage in new[] { "compose.publish", "present.return", "send.publish" })
            {
                var a = window.Where(e => e.Stage == stage).ToArray();
                metrics[stage] = new
                {
                    count = a.Length,
                    rate = end > 0 ? a.Length / end : 0,
                    uniqueImageCount = a.Select(e => e.ImageId).Distinct().Count(),
                    intervalMs = Stats(a.Zip(a.Skip(1), (x, y) => (y.Qpc - x.Qpc) * 1000.0 / Stopwatch.Frequency).ToArray()),
                    imageAgeMs = Stats(a.Where(e => e.GeneratedQpc > 0).Select(e => (e.Qpc - e.GeneratedQpc) * 1000.0 / Stopwatch.Frequency).ToArray())
                };
            }
            bool hasError = all.Any(e => e.Stage == "error");
            bool valid = run.Outcome == "completed" && origin > 0 && dropped == 0 && !hasError
                && window.Any(e => e.Stage == "compose.publish")
                && (!run.DisplayAttached || window.Any(e => e.Stage == "present.return"))
                && (!run.SpoutEnabled || window.Any(e => e.Stage == "send.publish"));
            WriteNew(Path.Combine(Directory!, "summary.json"), new
            {
                schemaVersion = 1,
                outcome = run.Outcome,
                appCpuSeconds = run.CpuSeconds,
                appCpuStartQpc = run.CpuStartQpc,
                appCpuEndQpc = run.CpuEndQpc,
                appCpuScope = "whole-run: engine startup through native cleanup; excludes log serialization",
                validPerformanceResult = valid,
                droppedEvents = dropped,
                capacity = capacity,
                eventCount = all.Length,
                analysisStartSeconds = 0.0,
                analysisEndSeconds = end,
                pool = new { capacity = pool.Capacity, pool.PeakReaders, pool.PeakOccupied },
                metrics,
                scanoutPending = scanout?.PendingCount,
                scanoutDisjoint = scanout?.Disjoint,
                sourceDiagnostics,
                limitation = "API/GPU publication timing only; no proof of unique receiver images or physical scanout."
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "出力トレースの保存に失敗しました: {Directory}", Directory);
        }
    }

    private static double? Percentile(double[] a, double p) => a.Length == 0 ? null : a.Order().ElementAt(Math.Min(a.Length - 1, (int)Math.Ceiling(a.Length * p) - 1));

    private static void WriteNew(string path, object data)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, data, Json);
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
}

internal sealed record OutputTraceRunSummary(
    string Outcome,
    bool DisplayAttached,
    bool SpoutEnabled,
    int CanvasWidth,
    int CanvasHeight,
    double PresentMarginMs,
    double ComposeLeadMs,
    string SenderName,
    double CpuSeconds,
    long CpuStartQpc,
    long CpuEndQpc,
    bool? VblankTimerHighResolution,
    bool GpuLoopTimerHighResolution,
    bool? SpoutLoopTimerHighResolution);
