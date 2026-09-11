using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Collections.Concurrent;

namespace GpuOutputProbe;

internal sealed record Options(string Mode, string Output, int Width, int Height, double Fps, double Seconds,
    double Warmup, string LogDir, string Sender, int MonitorIndex, bool Windowed, int MutexWaitMs = 0, double SendPhaseMs = 0, int PresentWaitMs = 0, string PresentWaitPlan = "fixed", string DisplayPacing = "tick", string CopyRetry = "off", string SourceSync = "keyed", double PresentMarginMs = 3, string ComposeAlign = "off", double ComposeLeadMs = 1.5, string Source = "pattern", int SourceWidth = 0, int SourceHeight = 0)
{
    public bool HasDisplay => Output is "fullscreen" or "both";
    public bool HasSpout => Output is "spout" or "both";
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool windowed = false;
        for (int i = 0; i < args.Length; i++)
        {
            string key = args[i];
            if (key == "--windowed") { windowed = true; continue; }
            if (!new[] { "--mode", "--output", "--width", "--height", "--fps", "--seconds", "--warmup", "--log-dir", "--sender", "--monitor-index", "--mutex-wait-ms", "--send-phase-ms", "--present-wait-ms", "--present-wait-plan", "--display-pacing", "--copy-retry", "--source-sync", "--present-margin-ms", "--compose-align", "--compose-lead-ms", "--source", "--source-size" }.Contains(key))
                throw new ArgumentException($"Unknown option: {key}");
            if (++i == args.Length || !values.TryAdd(key, args[i])) throw new ArgumentException($"Missing or duplicate option: {key}");
        }
        string Get(string key, string fallback) => values.GetValueOrDefault(key, fallback);
        int Int(string key, int fallback) => int.Parse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        double Num(string key, double fallback) => double.Parse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        var o = new Options(Get("--mode", "common"), Get("--output", "both"), Int("--width", 1920), Int("--height", 1080), Num("--fps", 60),
            Num("--seconds", 32), Num("--warmup", 5), Path.GetFullPath(Get("--log-dir", Path.Combine("probe-runs", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]))),
             Get("--sender", "GpuOutputProbe-" + Environment.ProcessId), Int("--monitor-index", 0), windowed, Int("--mutex-wait-ms", 0), Num("--send-phase-ms", 0), Int("--present-wait-ms", 0), Get("--present-wait-plan", "fixed"), Get("--display-pacing", "tick"), Get("--copy-retry", "off"), Get("--source-sync", "keyed"), Num("--present-margin-ms", 3), Get("--compose-align", "off"), Num("--compose-lead-ms", 1.5), Get("--source", "pattern"));
        // --source-size WxH: the contract-fake source image size (default: the canvas). Only meaningful with placement, i.e. contract-fake.
        string[] sourceSize = Get("--source-size", o.Width + "x" + o.Height).Split('x');
        if (sourceSize.Length != 2 || !int.TryParse(sourceSize[0], NumberStyles.None, CultureInfo.InvariantCulture, out int sourceWidth) || !int.TryParse(sourceSize[1], NumberStyles.None, CultureInfo.InvariantCulture, out int sourceHeight))
            throw new ArgumentException("Source size must be WxH with positive integers.");
        o = o with { SourceWidth = sourceWidth, SourceHeight = sourceHeight };
        if (o.Mode is not ("common" or "split") || o.Output is not ("fullscreen" or "spout" or "both")) throw new ArgumentException("Invalid mode/output.");
        if (o.Width is < 16 or > 8192 || o.Height is < 16 or > 8192 || !double.IsFinite(o.Fps) || o.Fps is < 1 or > 240 || !double.IsFinite(o.Seconds) || o.Seconds is < 1 or > 3600 ||
            !double.IsFinite(o.Warmup) || o.Warmup < 0 || 2 * o.Warmup >= o.Seconds || o.MonitorIndex < 0) throw new ArgumentException("Invalid dimensions/rate/duration/warmup/monitor.");
        if (o.Sender.Length is < 1 or > 200 || o.Sender.Any(c => c < 32 || c > 126 || c == '\\' || c == '/')) throw new ArgumentException("Sender must be 1..200 printable ASCII characters, without slashes.");
        if (o.MutexWaitMs is < 0 or > MutexWaitPolicy.MaxWaitMs) throw new ArgumentException($"Mutex wait must be an integer from 0 to {MutexWaitPolicy.MaxWaitMs} milliseconds.");
        if (!double.IsFinite(o.SendPhaseMs) || o.SendPhaseMs < 0 || o.SendPhaseMs >= 1000 / o.Fps)
            throw new ArgumentException("Send phase must be finite and satisfy 0 <= phase < one output period in milliseconds.");
        if (o.SendPhaseMs != 0 && (o.Mode != "split" || !o.HasSpout))
            throw new ArgumentException("A nonzero send phase requires split mode with Spout output.");
        if (o.PresentWaitMs is < 0 or > PresentReadyGate.MaxWaitMs || (o.PresentWaitMs != 0 && !o.HasDisplay))
            throw new ArgumentException("Present wait must be 0 or 1 milliseconds; nonzero requires fullscreen output.");
        if (o.PresentWaitPlan is not ("fixed" or "abba" or "baab")) throw new ArgumentException("Present wait plan must be fixed, abba, or baab.");
        if (o.PresentWaitPlan != "fixed" && (!o.HasDisplay || o.PresentWaitMs != 0 || o.Seconds / 4 <= 2 * o.Warmup))
            throw new ArgumentException("A segmented present wait plan requires fullscreen output, --present-wait-ms 0, and Seconds/4 > 2*Warmup.");
        if (o.DisplayPacing is not ("tick" or "ready" or "vsync" or "vblank")) throw new ArgumentException("Display pacing must be tick, ready, vsync, or vblank.");
        if (o.DisplayPacing == "ready" && (o.Mode != "split" || o.Output != "both" || o.PresentWaitMs != 0 || o.PresentWaitPlan != "fixed"))
            throw new ArgumentException("Ready display pacing requires split mode, both outputs, present-wait-ms 0, and fixed present-wait-plan.");
        if (o.DisplayPacing == "vsync" && (!o.HasDisplay || o.PresentWaitMs != 0 || o.PresentWaitPlan != "fixed"))
            throw new ArgumentException("Vsync display pacing requires fullscreen output, present-wait-ms 0, and fixed present-wait-plan.");
        if (o.DisplayPacing == "vblank" && (!o.HasDisplay || o.PresentWaitMs != 0 || o.PresentWaitPlan != "fixed"))
            throw new ArgumentException("Vblank display pacing requires fullscreen output, present-wait-ms 0, and fixed present-wait-plan.");
        if (!double.IsFinite(o.PresentMarginMs) || o.PresentMarginMs is < 0.5 or > 8) throw new ArgumentException("Present margin must be a finite value from 0.5 to 8 milliseconds.");
        if (o.PresentMarginMs != 3 && o.DisplayPacing != "vblank") throw new ArgumentException("A non-default present margin requires vblank display pacing.");
        if (o.ComposeAlign is not ("off" or "vblank")) throw new ArgumentException("Compose align must be off or vblank.");
        if (o.ComposeAlign == "vblank" && (o.DisplayPacing != "vblank" || !o.HasDisplay)) throw new ArgumentException("Vblank compose align requires vblank display pacing with fullscreen output.");
        if (!double.IsFinite(o.ComposeLeadMs) || o.ComposeLeadMs is < 0.5 or > 8) throw new ArgumentException("Compose lead must be a finite value from 0.5 to 8 milliseconds.");
        if (o.ComposeLeadMs != 1.5 && o.ComposeAlign != "vblank") throw new ArgumentException("A non-default compose lead requires vblank compose align.");
        if (o.Source is not ("pattern" or "contract-fake")) throw new ArgumentException("Source must be pattern or contract-fake.");
        if (o.SourceWidth is < 1 or > 8192 || o.SourceHeight is < 1 or > 8192) throw new ArgumentException("Source size must be 1..8192 x 1..8192.");
        if ((o.SourceWidth != o.Width || o.SourceHeight != o.Height) && o.Source != "contract-fake") throw new ArgumentException("A source size other than the canvas requires --source contract-fake.");
        if (o.CopyRetry is not ("off" or "signal")) throw new ArgumentException("Copy retry must be off or signal.");
        if (o.CopyRetry == "signal" && (o.Mode != "split" || !o.HasSpout)) throw new ArgumentException("Signal copy retry requires split mode with Spout output.");
        if (o.SourceSync is not ("keyed" or "fence")) throw new ArgumentException("Source sync must be keyed or fence.");
        if (o.SourceSync == "fence" && (o.Mode != "split" || !o.HasSpout)) throw new ArgumentException("Fence source sync requires split mode with Spout output.");
        if (o.SourceSync == "fence" && o.CopyRetry == "signal") throw new ArgumentException("Fence source sync has no keyed mutex to retry; use --copy-retry off.");
        return o;
    }
}

internal readonly record struct WorkerTiming(long OriginQpc, long EndQpc)
{
    public static WorkerTiming Create(Options options, long commonOrigin, long frequency, bool senderWorker)
    {
        long phaseTicks = senderWorker ? (long)Math.Round(options.SendPhaseMs * frequency / 1000) : 0;
        return new(commonOrigin + phaseTicks, commonOrigin + (long)(options.Seconds * frequency));
    }
    public bool AllowsWork(long now, bool cancelled) => !cancelled && now < EndQpc;
    public long WakeQpc(long nextDue) => Math.Min(nextDue, EndQpc);
}

// Phase offset shared by the GPU and Spout schedules (`--compose-align vblank`): written by the GPU worker, read by both.
internal sealed class ScheduleOffset
{
    private long ticks;
    public long Ticks => Volatile.Read(ref ticks);
    public void Add(long delta) => Interlocked.Add(ref ticks, delta);
}

// Tick i is due at origin + offset + round(i * period). The shared offset is sampled by DueQpc and that sample is used by
// Take, so an offset moved later between the loop's due check and Take cannot make a due tick "not due"; a change takes
// effect at the next DueQpc read, i.e. only for ticks not yet taken. Invariant: scheduled times strictly increase across
// Take (an offset moved earlier never hands out a time at or before the last taken one: such indices are skipped forward,
// never caught up, and are counted in Skipped like late ticks).
internal sealed class TickSchedule(long origin, double fps, long frequency, ScheduleOffset? offset = null)
{
    private long nextIndex, sampledOffset, lastScheduled = long.MinValue;
    private long At(long index) => origin + sampledOffset + (long)Math.Round(index * frequency / fps);
    private long First() { long index = nextIndex; while (At(index) <= lastScheduled) index++; return index; }
    public long DueQpc { get { sampledOffset = offset?.Ticks ?? 0; return At(First()); } }
    public (long Scheduled, long Skipped) Take(long now)
    {
        if (now < At(First())) throw new InvalidOperationException("Tick is not due.");
        long current = Math.Max(First(), (long)Math.Floor((now - origin - sampledOffset) * fps / frequency));
        long skipped = current - nextIndex;
        long scheduled = At(current);
        nextIndex = current + 1; lastScheduled = scheduled;
        return (scheduled, skipped);
    }
}

internal readonly record struct ImageStamp(long Id, long GeneratedQpc);
internal sealed class LatestPool(int count)
{
    private readonly object gate = new();
    private readonly bool[] writing = new bool[count];
    private readonly int[] readers = new int[count];
    private readonly ImageStamp[] stamps = new ImageStamp[count];
    private int latest = -1;
    public int PeakReaders { get; private set; }
    public int PeakOccupied { get; private set; }
    public int TryBeginWrite()
    {
        lock (gate)
        {
            for (int i = 0; i < count; i++)
                if (i != latest && !writing[i] && readers[i] == 0) { writing[i] = true; Track(); return i; }
            return -1;
        }
    }
    public void Publish(int slot, ImageStamp stamp, bool gpuComplete)
    {
        lock (gate)
        {
            if (!writing[slot] || !gpuComplete) throw new InvalidOperationException("Cannot publish incomplete image.");
            writing[slot] = false; stamps[slot] = stamp; latest = slot; Track();
        }
    }
    public void AbortWrite(int slot, bool gpuComplete)
    {
        lock (gate) { if (!writing[slot] || !gpuComplete) throw new InvalidOperationException("Write not active or GPU still owns write."); writing[slot] = false; }
    }
    public long LatestId { get { lock (gate) return latest < 0 ? 0 : stamps[latest].Id; } }
    public Lease? AcquireLatest()
    {
        lock (gate)
        {
            if (latest < 0) return null;
            readers[latest]++; Track(); return new Lease(this, latest, stamps[latest]);
        }
    }
    private void Track()
    {
        PeakReaders = Math.Max(PeakReaders, readers.Sum());
        PeakOccupied = Math.Max(PeakOccupied, Enumerable.Range(0, count).Count(i => writing[i] || readers[i] > 0 || latest == i));
    }
    public sealed class Lease(LatestPool pool, int slot, ImageStamp stamp) : IDisposable
    {
        public int Slot { get; } = slot;
        public ImageStamp Stamp { get; } = stamp;
        private bool inFlight, disposed;
        public void BeginGpuUse() { if (disposed || inFlight) throw new InvalidOperationException(); inFlight = true; }
        public void CompleteGpuUse() { if (disposed || !inFlight) throw new InvalidOperationException("No active GPU use."); inFlight = false; }
        public void Dispose()
        {
            lock (pool.gate)
            {
                if (inFlight) throw new InvalidOperationException("GPU use must complete before lease return.");
                if (!disposed) { pool.readers[Slot]--; disposed = true; }
            }
        }
    }
}

internal sealed record ProbeEvent(string Stage, string Worker, long Qpc, long ScheduledQpc = 0, long ImageId = 0, long GeneratedQpc = 0, string? Detail = null, long Value = 0, long DeadlineQpc = 0);
internal sealed record ProcessCpuSample(long StartQpc, long EndQpc, double CpuSeconds);
internal sealed class ProbeLog
{
    private const int Capacity = 1_000_000;
    private readonly ConcurrentQueue<ProbeEvent> events = new();
    private int count, dropped;
    public long OriginQpc { get; set; }
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
    public void Add(string stage, string worker, long scheduled = 0, ImageStamp stamp = default, string? detail = null, long value = 0)
    {
        Record(new(stage, worker, Stopwatch.GetTimestamp(), scheduled, stamp.Id, stamp.GeneratedQpc, detail, value));
    }
    internal void Record(ProbeEvent item)
    {
        if (Interlocked.Increment(ref count) <= Capacity) events.Enqueue(item);
        else Interlocked.Increment(ref dropped);
    }
    public void Save(Options options, string outcome, LatestPool pool, ProcessCpuSample? cpu = null, ScanoutTracker? scanout = null, SourceDiagnostics? sourceDiagnostics = null)
    {
        var all = events.OrderBy(e => e.Qpc).ToArray();
        using (var writer = new StreamWriter(new FileStream(Path.Combine(options.LogDir, "events.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)))
            foreach (var e in all) writer.WriteLine(JsonSerializer.Serialize(e, Json));
        double end = options.Seconds - options.Warmup;
        var window = all.Where(e => e.Qpc >= OriginQpc + options.Warmup * Stopwatch.Frequency && e.Qpc < OriginQpc + end * Stopwatch.Frequency).ToArray();
        object Stats(double[] a) => new { count = a.Length, mean = a.Length == 0 ? (double?)null : a.Average(), p95 = Percentile(a, .95), p99 = Percentile(a, .99), max = a.Length == 0 ? (double?)null : a.Max() };
        var metrics = new Dictionary<string, object>();
        foreach (string stage in new[] { "compose.publish", "present.return", "send.publish" })
        {
            var a = window.Where(e => e.Stage == stage).ToArray();
            metrics[stage] = new { count = a.Length, rate = a.Length / (end - options.Warmup), uniqueImageCount = a.Select(e => e.ImageId).Distinct().Count(),
                intervalMs = Stats(a.Zip(a.Skip(1), (x, y) => (y.Qpc - x.Qpc) * 1000.0 / Stopwatch.Frequency).ToArray()),
                imageAgeMs = Stats(a.Where(e => e.GeneratedQpc > 0).Select(e => (e.Qpc - e.GeneratedQpc) * 1000.0 / Stopwatch.Frequency).ToArray()) };
        }
        WriteNew(Path.Combine(options.LogDir, "summary.json"), new { schemaVersion = 1, outcome,
            appCpuSeconds = cpu?.CpuSeconds, appCpuStartQpc = cpu?.StartQpc, appCpuEndQpc = cpu?.EndQpc,
            appCpuScope = cpu == null ? "not measured" : "whole-run: engine startup through native cleanup; excludes log serialization",
            validPerformanceResult = outcome == "completed" && OriginQpc > 0 && all.Any(e => e.Qpc >= OriginQpc + options.Seconds * Stopwatch.Frequency) && dropped == 0 && !all.Any(e => e.Stage == "error") && window.Any(e => e.Stage == "compose.publish") && (!options.HasDisplay || window.Any(e => e.Stage == "present.return")) && (!options.HasSpout || window.Any(e => e.Stage == "send.publish")),
            droppedEvents = dropped, eventCount = all.Length, analysisStartSeconds = options.Warmup, analysisEndSeconds = end,
            pool = new { capacity = 3, pool.PeakReaders, pool.PeakOccupied }, metrics,
            scanoutPending = scanout?.PendingCount, scanoutDisjoint = scanout?.Disjoint, sourceDiagnostics,
            limitation = "API/GPU publication timing only; no proof of unique receiver images or physical scanout." });
    }
    private static double? Percentile(double[] a, double p) => a.Length == 0 ? null : a.Order().ElementAt(Math.Min(a.Length - 1, (int)Math.Ceiling(a.Length * p) - 1));
    public static void WriteNew(string path, object data)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, data, Json);
    }
}

