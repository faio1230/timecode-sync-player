using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// Opt-in baseline observer. Producers only probe a few pixels and try a bounded queue;
/// JSON serialization and disk I/O run on one background consumer.
/// </summary>
internal sealed class SyncAccuracyTrace : IDisposable
{
    internal const string EnvironmentVariable = "TIMECODE_ACCURACY_TRACE";
    /// <summary>計測用の LTC 参照 fps（V3 の 24/25/29.97/30 マトリクス）。未設定・不正は 25。</summary>
    internal const string ReferenceFpsEnvironmentVariable = "TIMECODE_ACCURACY_LTC_FPS";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();
    private readonly double _referenceLtcFps;
    private readonly Channel<object>? _queue;
    private readonly Task? _writerTask;
    private bool _closed;
    private long _dropped;
    private long _errors;
    private long _events;
    private long _renderSessions;

    public static SyncAccuracyTrace Disabled { get; } = new(25.0);
    public static SyncAccuracyTrace Current { get; set; } = Disabled;
    public bool IsEnabled => _queue != null;

    private SyncAccuracyTrace(double referenceLtcFps) => _referenceLtcFps = referenceLtcFps;

    private SyncAccuracyTrace(StreamWriter writer, int capacity, double referenceLtcFps)
    {
        _referenceLtcFps = referenceLtcFps;
        _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        long started = Stopwatch.GetTimestamp();
        _writerTask = Task.Run(() => WriteAsync(writer, started));
    }

    public static SyncAccuracyTrace Create(string? path, int capacity = 8192)
        => Create(path, capacity,
            ParseReferenceLtcFps(Environment.GetEnvironmentVariable(ReferenceFpsEnvironmentVariable)));

    /// <summary>
    /// referenceLtcFps は計測対象の LTC レート（24/25/29.97/30）。アプリ側の解決 fps とは独立に、
    /// 記録する LTC 秒と meta の nominalLtcFps を決める。
    /// </summary>
    internal static SyncAccuracyTrace Create(string? path, int capacity, double referenceLtcFps)
    {
        if (string.IsNullOrWhiteSpace(path)) return Disabled;
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        try
        {
            // Never erase a prior measurement, including on an accidental second launch.
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            return new SyncAccuracyTrace(new StreamWriter(stream), capacity, referenceLtcFps);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Error(ex, "Cannot create sync accuracy trace {Path}; measurement is unavailable", path);
            return Disabled;
        }
    }

    /// <summary>環境変数の解釈。不正値・範囲外は 25 に落とす（従来の計測と同じ）。</summary>
    internal static double ParseReferenceLtcFps(string? value)
        => double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double fps)
            && fps is >= 1.0 and <= 1000.0
            ? fps
            : 25.0;

    public void RecordLtc(LtcFrameReceivedEventArgs frame)
    {
        if (!IsEnabled) return;
        long ticks = Stopwatch.GetTimestamp();
        // 計測用の参照 fps はハーネス（V3 LTC fps マトリクス）が与える。デコーダの過渡推定や
        // アプリが独立に解決した同期 fps の代用はしない（記録の一貫性を崩さないため）。
        // 換算はアプリ本体と同じ LtcTimecode.ToRealSeconds を使い、29.97 の総フレーム換算も揃える。
        Enqueue(new LtcEvent("ltc", ticks, frame.Timecode.ToRealSeconds(_referenceLtcFps), _referenceLtcFps));
    }

    internal long AllocateRenderSessionId() => IsEnabled ? Interlocked.Increment(ref _renderSessions) : 0;

    internal void RecordRenderStage(long sessionId, long? attemptId, int generation, long? sequence,
        string stage, string outcome, long startTicks, long endTicks, int width, int height, int? returnCode = null)
    {
        if (!IsEnabled) return;
        try
        {
            Enqueue(new RenderStageEvent("render-stage", endTicks, sessionId, attemptId, generation, sequence,
                stage, outcome, startTicks, endTicks, width, height, returnCode, Environment.CurrentManagedThreadId));
        }
        catch (Exception)
        {
            // Observers never interrupt rendering, publication or pooled-buffer cleanup.
            Interlocked.Increment(ref _errors);
        }
    }

    public void RecordFrame(string kind, IntPtr pixels, int width, int height, int stride, long publishedTicks)
        => RecordPixelFrame("frame", kind, pixels, width, height, stride, publishedTicks);

    /// <summary>
    /// A1: GPU 経路の計測。GPU worker が公開直後にソース画素を読み戻して得た Probe をそのまま記録する
    /// （CPU の bitmap-publication と同じ境界・同じマーカー判定。計測有効時のみ呼ばれる）。
    /// </summary>
    public void RecordGpuFrame(string kind, int width, int height, AccuracyFrameProbe probe,
        long publishedTicks, long probeTicks)
    {
        if (!IsEnabled) return;
        Enqueue(new FrameEvent("frame", publishedTicks, kind, width, height,
            probe.ClipId, probe.FrameIndex, probe.IsBlack, probe.MarkerValid, probeTicks));
    }

    public void RecordPreviewFrame(string kind, IntPtr pixels, int width, int height, int stride, long publishedTicks)
        => RecordPixelFrame("preview-frame", kind, pixels, width, height, stride, publishedTicks);

    private void RecordPixelFrame(string type, string kind, IntPtr pixels, int width, int height, int stride, long publishedTicks)
    {
        if (!IsEnabled) return;
        long before = Stopwatch.GetTimestamp();
        try
        {
            var probe = AccuracyFrameMarker.Probe(pixels, width, height, stride);
            long probeTicks = Stopwatch.GetTimestamp() - before;
            Enqueue(new FrameEvent(type, publishedTicks, kind, width, height,
                probe.ClipId, probe.FrameIndex, probe.IsBlack, probe.MarkerValid, probeTicks));
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            Log.Error(ex, "Sync accuracy pixel probe failed");
        }
    }

    private void Enqueue(object value)
    {
        lock (_gate)
        {
            if (_closed) return;
            // TryWrite with Wait mode reports a full queue without discarding an older event.
            if (!_queue!.Writer.TryWrite(value)) Interlocked.Increment(ref _dropped);
        }
    }

    private async Task WriteAsync(StreamWriter writer, long started)
    {
        using (writer)
        {
            try
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    Type = "meta", Ticks = started, Schema = 1, Frequency = Stopwatch.Frequency,
                    Boundary = "bitmap-publication", Reference = "decoded-ltc-receipt",
                    NominalLtcFps = _referenceLtcFps,
                    BlackProbe = "9x9-grid-and-marker-centers", RenderStageSchema = 1,
                    PreviewFrameSchema = 1, PreviewBoundary = "reduced-preview-bitmap; separate from full-resolution frame events",
                    RenderStageMeasure = "native-render is the entire mpv render call, not decoder-only; stages overlap across threads; join by session/generation/sequence, attempt for unpublished native work"
                }, JsonOptions)).ConfigureAwait(false);
                // Arrival order may differ from ticks across producers. Consumers stably sort by ticks.
                await foreach (object value in _queue!.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions)).ConfigureAwait(false);
                    _events++;
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    Type = "end", Ticks = Stopwatch.GetTimestamp(),
                    Dropped = Interlocked.Read(ref _dropped), Errors = Interlocked.Read(ref _errors), Events = _events
                }, JsonOptions)).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errors);
                // A failed output may be unwritable even for 'end'; missing end explicitly invalidates the run.
                Log.Error(ex, "Sync accuracy trace write failed; measurement is incomplete");
            }
        }
    }

    public void Dispose()
    {
        if (!IsEnabled) return;
        lock (_gate)
        {
            if (!_closed)
            {
                _closed = true;
                _queue!.Writer.TryComplete();
            }
        }
        // No UI dispatcher dependency. Shutdown waits for all accepted events and the final summary.
        try { _writerTask!.GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Error(ex, "Sync accuracy trace close failed; measurement is incomplete"); }
    }

    private sealed record LtcEvent(string Type, long Ticks, double Seconds, double Fps);
    private sealed record FrameEvent(string Type, long Ticks, string Kind, int Width, int Height,
        int? ClipId, int? FrameIndex, bool IsBlack, bool MarkerValid, long ProbeTicks);
    private sealed record RenderStageEvent(string Type, long Ticks, long SessionId, long? AttemptId,
        int Generation, long? Sequence, string Stage, string Outcome, long StartTicks, long EndTicks,
        int Width, int Height, int? ReturnCode, int ThreadId);
}
