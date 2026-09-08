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
    private const double ReferenceLtcFps = 25;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();
    private readonly Channel<object>? _queue;
    private readonly Task? _writerTask;
    private bool _closed;
    private long _dropped;
    private long _errors;
    private long _events;
    private long _renderSessions;

    public static SyncAccuracyTrace Disabled { get; } = new();
    public static SyncAccuracyTrace Current { get; set; } = Disabled;
    public bool IsEnabled => _queue != null;

    private SyncAccuracyTrace() { }

    private SyncAccuracyTrace(StreamWriter writer, int capacity)
    {
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
    {
        if (string.IsNullOrWhiteSpace(path)) return Disabled;
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        try
        {
            // Never erase a prior measurement, including on an accidental second launch.
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            return new SyncAccuracyTrace(new StreamWriter(stream), capacity);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Log.Error(ex, "Cannot create sync accuracy trace {Path}; measurement is unavailable", path);
            return Disabled;
        }
    }

    public void RecordLtc(LtcFrameReceivedEventArgs frame)
    {
        if (!IsEnabled) return;
        long ticks = Stopwatch.GetTimestamp();
        // This observer's approved fixture is nominal 25 fps. Do not substitute the decoder's
        // transient estimate or mutate the application's independently resolved synchronization fps.
        double seconds = frame.Timecode.Hours * 3600.0 + frame.Timecode.Minutes * 60.0
            + frame.Timecode.Seconds + frame.Timecode.Frames / ReferenceLtcFps;
        Enqueue(new LtcEvent("ltc", ticks, seconds, ReferenceLtcFps));
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
    {
        if (!IsEnabled) return;
        long before = Stopwatch.GetTimestamp();
        try
        {
            var probe = AccuracyFrameMarker.Probe(pixels, width, height, stride);
            long probeTicks = Stopwatch.GetTimestamp() - before;
            Enqueue(new FrameEvent("frame", publishedTicks, kind, width, height,
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
                    NominalLtcFps = ReferenceLtcFps,
                    BlackProbe = "9x9-grid-and-marker-centers", RenderStageSchema = 1,
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
