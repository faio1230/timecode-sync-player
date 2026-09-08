using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer;

internal sealed class RenderFramePublishPipeline
{
    private readonly Func<byte[], int, int, double> _updateDisplay;
    private readonly Func<IntPtr, int, int, double> _publishSpout;
    private readonly Action<RenderFramePerformanceMeasurement> _recordPerformance;
    private readonly Func<byte[], GapState, int, int, bool> _copyFreezeFrame;

    public RenderFramePublishPipeline(
        Func<byte[], int, int, double> updateDisplay,
        Func<IntPtr, int, int, double> publishSpout,
        Action<RenderFramePerformanceMeasurement> recordPerformance,
        Func<byte[], GapState, int, int, bool> copyFreezeFrame)
    {
        _updateDisplay = updateDisplay;
        _publishSpout = publishSpout;
        _recordPerformance = recordPerformance;
        _copyFreezeFrame = copyFreezeFrame;
    }

    public void Publish(
        byte[] pixels,
        int width,
        int height,
        double renderMs,
        bool spoutEnabled,
        GapState gapState, SyncAccuracyTrace? trace = null, long sessionId = 0, int generation = 0, long sequence = 0)
    {
        if (trace?.IsEnabled == true)
        {
            PublishTraced(pixels, width, height, renderMs, spoutEnabled, gapState, trace, sessionId, generation, sequence);
            return;
        }
        double bitmapMs = _updateDisplay(pixels, width, height);
        double spoutMs = PublishSpout(pixels, width, height);
        _recordPerformance(new RenderFramePerformanceMeasurement(
            renderMs,
            bitmapMs,
            spoutMs,
            width,
            height,
            spoutEnabled));
        _copyFreezeFrame(pixels, gapState, width, height);
    }

    private double PublishSpout(byte[] pixels, int width, int height)
    {
        // The session owns the snapshot until synchronous publication finishes. Spout
        // consumes this CPU pointer during SendFrame; it must not retain it after return.
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { return _publishSpout(pin.AddrOfPinnedObject(), width, height); }
        finally { pin.Free(); }
    }

    private void PublishTraced(byte[] pixels, int width, int height, double renderMs, bool spoutEnabled,
        GapState gapState, SyncAccuracyTrace trace, long sessionId, int generation, long sequence)
    {
        double bitmapMs, spoutMs;
        long start = Stopwatch.GetTimestamp();
        bool succeeded = false;
        try
        {
            using var bitmapScope = new BitmapRenderTraceScope(trace, sessionId, generation, sequence, width, height);
            bitmapMs = _updateDisplay(pixels, width, height);
            succeeded = true;
        }
        finally { trace.RecordRenderStage(sessionId, null, generation, sequence, "bitmap", succeeded ? "completed" : "exception", start, Stopwatch.GetTimestamp(), width, height); }
        start = Stopwatch.GetTimestamp(); succeeded = false;
        try { spoutMs = PublishSpout(pixels, width, height); succeeded = true; }
        finally { trace.RecordRenderStage(sessionId, null, generation, sequence, "spout", !succeeded ? "exception" : spoutEnabled ? "call-returned" : "disabled-call-returned", start, Stopwatch.GetTimestamp(), width, height); }
        // Keep these legacy counters restricted to published frames.
        _recordPerformance(new RenderFramePerformanceMeasurement(renderMs, bitmapMs, spoutMs, width, height, spoutEnabled));
        start = Stopwatch.GetTimestamp(); succeeded = false; bool copied = false;
        try { copied = _copyFreezeFrame(pixels, gapState, width, height); succeeded = true; }
        finally { trace.RecordRenderStage(sessionId, null, generation, sequence, "freeze-copy", !succeeded ? "exception" : copied ? "copied" : "not-needed", start, Stopwatch.GetTimestamp(), width, height); }
    }
}
