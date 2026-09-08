using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TimecodeSyncPlayer;

// Exercise the product transport directly, without starting WPF, mpv or LTC.
const int width = 3840, height = 2160, fps = 60, seconds = 30;
if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: SpoutTransportProbe.exe <output.jsonl>");
    return 2;
}

string output;
try
{
    output = Path.GetFullPath(args[0]);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    // Validate output access before allocating buffers or starting the sender.
    using var outputCheck = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Cannot create output: {ex.Message}");
    return 2;
}

byte[] colors = [64, 128, 192];
var diagnosticSink = new MemoryDiagnosticSink();
using var diagnosticLogger = new LoggerConfiguration()
    .MinimumLevel.Warning()
    .WriteTo.Sink(diagnosticSink)
    .CreateLogger();
Log.Logger = diagnosticLogger;
GCHandle[] handles = new GCHandle[colors.Length];
var records = new List<SendRecord>(fps * seconds);
using var process = Process.GetCurrentProcess();
long frequency = Stopwatch.Frequency;
long start = 0, ended = 0, missedSlots = 0;
double cpuStart = 0, cpuEnd = 0;
int gc0 = 0, gc1 = 0, gc2 = 0;
int gc0Delta = 0, gc1Delta = 0, gc2Delta = 0;
bool initialized = false, disposed = false, completed = false;
string? error = null;
int exitCode = 0;
var sender = new SpoutOutput();
try
{
    for (int i = 0; i < colors.Length; i++)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = colors[i];
            pixels[offset + 3] = 255;
        }
        // These three buffers remain immutable and pinned until sender.Dispose.
        handles[i] = GCHandle.Alloc(pixels, GCHandleType.Pinned);
    }
    initialized = sender.TryInitialize();
    if (!initialized) throw new InvalidOperationException("Product SpoutOutput initialization failed.");
    sender.IsEnabled = true;
    gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
    cpuStart = process.TotalProcessorTime.TotalSeconds;
    start = Stopwatch.GetTimestamp();
    long stopAt = start + frequency * seconds;
    for (int slot = 0; slot < fps * seconds; slot++)
    {
        long due = start + slot * frequency / fps;
        WaitUntil(due, frequency);
        long before = Stopwatch.GetTimestamp();
        if (before >= stopAt) { missedSlots += fps * seconds - slot; break; }
        // Drop expired scheduled slots instead of sending a catch-up burst.
        int currentSlot = Math.Min(fps * seconds - 1, (int)((before - start) * fps / frequency));
        if (currentSlot > slot)
        {
            missedSlots += currentSlot - slot;
            slot = currentSlot;
            due = start + slot * frequency / fps;
        }
        int colorIndex = slot % colors.Length;
        bool availableBefore = sender.IsAvailable, enabledBefore = sender.IsEnabled;
        long sendStart = Stopwatch.GetTimestamp();
        sender.SendFrame(handles[colorIndex].AddrOfPinnedObject(), width, height);
        long sendEnd = Stopwatch.GetTimestamp();
        records.Add(new SendRecord(records.Count, slot, colorIndex, colors[colorIndex], due,
            sendStart, sendEnd, (sendEnd - sendStart) * 1000.0 / frequency,
            (sendStart - due) * 1000.0 / frequency, availableBefore, enabledBefore,
            sender.IsAvailable, sender.IsEnabled));
        if (!sender.IsAvailable || !sender.IsEnabled)
            throw new InvalidOperationException("Product SpoutOutput became unavailable/disabled during send.");
    }
    WaitUntil(stopAt, frequency);
    completed = true;
}
catch (Exception ex) { error = ex.ToString(); exitCode = 1; }
finally
{
    ended = Stopwatch.GetTimestamp();
    cpuEnd = process.TotalProcessorTime.TotalSeconds;
    if (start > 0)
    {
        gc0Delta = GC.CollectionCount(0) - gc0;
        gc1Delta = GC.CollectionCount(1) - gc1;
        gc2Delta = GC.CollectionCount(2) - gc2;
    }
    // All sender calls occur on this thread; pins survive native destruction.
    try { sender.Dispose(); disposed = true; }
    catch (Exception ex) { error = (error ?? "") + "\nDispose: " + ex; exitCode = 1; }
    if (disposed)
        foreach (GCHandle handle in handles) if (handle.IsAllocated) handle.Free();
}

if (!completed || !disposed) exitCode = 1;
// Warning/error events are queued in memory; render and write them only after timing.
try
{
    using var writer = new StreamWriter(output, false);
    writer.WriteLine(JsonSerializer.Serialize(new { @event = "start", mode = "baseline", width, height, fps, seconds,
        colors, alpha = 255, requestedSenderName = SpoutOutput.DefaultSenderName, qpcFrequency = frequency,
        startQpc = start, processId = Environment.ProcessId, initialized,
        cpuMeasure = "process CPU seconds includes pacing and sends, excludes buffer setup/dispose/serialization",
        transportMissMeasure = "unavailable: product SendFrame returns void; receiver observation is required" }));
    foreach (SendRecord row in records)
        writer.WriteLine(JsonSerializer.Serialize(new { @event = "send", row.FrameIndex, row.Slot,
            row.ColorIndex, row.Color, alpha = 255, row.DueQpc, row.SendStartQpc, row.SendEndQpc,
            row.SendMs, row.LateMs, row.AvailableBefore, row.EnabledBefore, row.AvailableAfter, row.EnabledAfter }));
    double wall = start > 0 ? (ended - start) / (double)frequency : 0;
    double cpu = start > 0 ? cpuEnd - cpuStart : 0;
    writer.WriteLine(JsonSerializer.Serialize(new { @event = "summary", mode = "baseline", endQpc = ended, wallSeconds = wall,
        attemptedFrames = records.Count, missedScheduledSlots = missedSlots,
        unavailableAfterSend = records.Count(x => !x.AvailableAfter || !x.EnabledAfter),
        transportMisses = (int?)null, receiverRequired = true, processCpuSeconds = cpu,
        cpuOneCorePercent = wall > 0 ? 100 * cpu / wall : 0,
        sendTotalMs = records.Sum(x => x.SendMs), sendMaxMs = records.Count > 0 ? records.Max(x => x.SendMs) : 0,
        sendOverBudget = records.Count(x => x.SendMs > 1000.0 / fps),
        gc0 = gc0Delta, gc1 = gc1Delta, gc2 = gc2Delta,
        diagnosticLogs = diagnosticSink.Events.Select(logEvent => new
        {
            timestamp = logEvent.Timestamp,
            level = logEvent.Level.ToString(),
            messageTemplate = logEvent.MessageTemplate.Text,
            message = logEvent.RenderMessage(),
            properties = logEvent.Properties.ToDictionary(property => property.Key,
                property => DiagnosticValue(property.Value)),
            exception = logEvent.Exception?.ToString()
        }).ToArray(),
        completed, disposed, error, exitCode }));
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Cannot write output: {ex.Message}");
    return 1;
}
Console.WriteLine($"{output}: attempts={records.Count}, scheduleMisses={missedSlots}, completed={completed}, disposed={disposed}, exit={exitCode}; receiver validation required");
return exitCode;

// Preserve typed stage timings and QPC values for analysis without parsing the
// rendered message. This conversion runs only after the measurement has ended.
static object? DiagnosticValue(LogEventPropertyValue value) => value switch
{
    ScalarValue scalar => scalar.Value,
    StructureValue structure => structure.Properties.ToDictionary(property => property.Name,
        property => DiagnosticValue(property.Value)),
    SequenceValue sequence => sequence.Elements.Select(DiagnosticValue).ToArray(),
    _ => value.ToString()
};

static void WaitUntil(long target, long frequency)
{
    while (true)
    {
        long remaining = target - Stopwatch.GetTimestamp();
        if (remaining <= 0) return;
        double ms = remaining * 1000.0 / frequency;
        if (ms > 2) Thread.Sleep(Math.Max(1, (int)ms - 1));
        else Thread.SpinWait(32);
    }
}

readonly record struct SendRecord(int FrameIndex, int Slot, int ColorIndex, byte Color,
    long DueQpc, long SendStartQpc, long SendEndQpc, double SendMs, double LateMs,
    bool AvailableBefore, bool EnabledBefore, bool AvailableAfter, bool EnabledAfter);

sealed class MemoryDiagnosticSink : ILogEventSink
{
    public ConcurrentQueue<LogEvent> Events { get; } = new();

    public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
}
