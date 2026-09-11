using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Serilog;

namespace TimecodeSyncPlayer.Output;

internal sealed record OutputTraceEvent(string Stage, string Worker, long Qpc, long ScheduledQpc = 0, long ImageId = 0, long GeneratedQpc = 0, string? Detail = null, long Value = 0, long DeadlineQpc = 0);

/// <summary>
/// 出力トレース（試作 GpuOutputProbe の manifest.json / events.jsonl / summary.json と同スキーマ）。
/// 環境変数 TIMECODE_SYNC_PLAYER_OUTPUT_TRACE にディレクトリを指定したときだけ有効。
/// 試作と同じくイベントはメモリに溜め、停止時にまとめて書く（強制終了時は残らない）。
/// </summary>
internal sealed class OutputTrace
{
    public const string EnvironmentVariable = "TIMECODE_SYNC_PLAYER_OUTPUT_TRACE";
    private const int Capacity = 1_000_000;
    private readonly ConcurrentQueue<OutputTraceEvent> events = new();
    private int count, dropped;
    public long OriginQpc { get; set; }
    public string? Directory { get; }
    public bool IsEnabled => Directory != null;

    internal static OutputTrace Disabled { get; } = new();

    private OutputTrace() { }

    private OutputTrace(string directory) { Directory = directory; }

    public static OutputTrace Create(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return Disabled;
        try
        {
            string full = Path.GetFullPath(directory);
            System.IO.Directory.CreateDirectory(full);
            return new OutputTrace(full);
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
        if (Interlocked.Increment(ref count) <= Capacity) events.Enqueue(item);
        else Interlocked.Increment(ref dropped);
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
