using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace TimecodeSyncPlayer;

internal sealed class RenderFramePublishPipeline
{
    private readonly Func<byte[], int, int, double> _updateDisplay;
    private readonly Func<IntPtr, int, int, double> _publishSpout;
    private readonly Action<RenderFramePerformanceMeasurement> _recordPerformance;
    private readonly Func<byte[], GapState, int, int, bool> _copyFreezeFrame;
    private readonly Action? _queuePreview;
    private readonly Action? _cancelPreview;
    private readonly Func<byte[], int, int, Func<double>, (double BitmapMs, double SpoutMs)>? _updateDisplayWithSpout;

    public RenderFramePublishPipeline(
        Func<byte[], int, int, double> updateDisplay,
        Func<IntPtr, int, int, double> publishSpout,
        Action<RenderFramePerformanceMeasurement> recordPerformance,
        Func<byte[], GapState, int, int, bool> copyFreezeFrame,
        Action? queuePreview = null, Action? cancelPreview = null,
        Func<byte[], int, int, Func<double>, (double BitmapMs, double SpoutMs)>? updateDisplayWithSpout = null)
    {
        _updateDisplay = updateDisplay;
        _publishSpout = publishSpout;
        _recordPerformance = recordPerformance;
        _copyFreezeFrame = copyFreezeFrame;
        _queuePreview = queuePreview;
        _cancelPreview = cancelPreview;
        _updateDisplayWithSpout = updateDisplayWithSpout;
    }

    public void Publish(
        byte[] pixels,
        int width,
        int height,
        double renderMs,
        bool spoutEnabled,
        GapState gapState, SyncAccuracyTrace? trace = null, long sessionId = 0, int generation = 0, long sequence = 0,
        bool combineBitmapAndSpout = false)
    {
        try { PublishCore(pixels, width, height, renderMs, spoutEnabled, gapState, trace, sessionId, generation, sequence, combineBitmapAndSpout); }
        catch
        {
            // A retained mutable bitmap may have changed before a later stage
            // failed. Do not let an older pending preview read that failed update.
            try { _cancelPreview?.Invoke(); }
            catch (Exception ex)
            {
                try { Log.Warning(ex, "Preview cancellation failed after external publication failure"); }
                catch (Exception) { }
            }
            throw;
        }
    }

    private void PublishCore(byte[] pixels, int width, int height, double renderMs, bool spoutEnabled,
        GapState gapState, SyncAccuracyTrace? trace, long sessionId, int generation, long sequence, bool combineBitmapAndSpout)
    {
        if (trace?.IsEnabled == true)
        {
            PublishTraced(pixels, width, height, renderMs, spoutEnabled, gapState, trace, sessionId, generation, sequence, combineBitmapAndSpout);
            return;
        }
        double bitmapMs, spoutMs;
        if (combineBitmapAndSpout && spoutEnabled && _updateDisplayWithSpout != null)
            (bitmapMs, spoutMs) = PublishCombined(pixels, width, height, null, sessionId, generation, sequence);
        else
        {
            bitmapMs = _updateDisplay(pixels, width, height);
            spoutMs = PublishSpout(pixels, width, height);
        }
        _recordPerformance(new RenderFramePerformanceMeasurement(
            renderMs,
            bitmapMs,
            spoutMs,
            width,
            height,
            spoutEnabled));
        _copyFreezeFrame(pixels, gapState, width, height);
        QueuePreview();
    }

    private bool QueuePreview()
    {
        try { _queuePreview?.Invoke(); return true; }
        catch (Exception ex)
        {
            try { Log.Warning(ex, "Preview callback failed after external frame publication"); }
            catch (Exception) { /* Preview/logging cannot invalidate external publication. */ }
            return false;
        }
    }

    private double PublishSpout(byte[] pixels, int width, int height)
    {
        // The session owns the snapshot until synchronous publication finishes. Spout
        // consumes this CPU pointer during SendFrame; it must not retain it after return.
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { return _publishSpout(pin.AddrOfPinnedObject(), width, height); }
        finally { pin.Free(); }
    }

    private (double BitmapMs, double SpoutMs) PublishCombined(byte[] pixels, int width, int height,
        SyncAccuracyTrace? trace, long sessionId, int generation, long sequence)
    {
        long scopeStart = trace != null ? Stopwatch.GetTimestamp() : 0;
        long sendStart = 0, sendEnd = 0;
        bool succeeded = false, sent = false;
        try
        {
            using var bitmapScope = trace != null ? new BitmapRenderTraceScope(trace, sessionId, generation, sequence, width, height) : null;
            var result = _updateDisplayWithSpout!(pixels, width, height, () =>
            {
                if (trace == null) return PublishSpout(pixels, width, height);
                sendStart = Stopwatch.GetTimestamp();
                try { double elapsed = PublishSpout(pixels, width, height); sent = true; return elapsed; }
                finally { sendEnd = Stopwatch.GetTimestamp(); }
            });
            succeeded = true;
            return result;
        }
        finally
        {
            // The lexical bitmap helper has attempted Unlock before returning or
            // throwing. Only now enqueue observers, including the send interval.
            if (trace != null)
            {
                if (sendStart != 0) trace.RecordRenderStage(sessionId, null, generation, sequence, "spout",
                    sent ? "call-returned" : "exception", sendStart, sendEnd, width, height);
                trace.RecordRenderStage(sessionId, null, generation, sequence, "bitmap-send-scope",
                    succeeded ? "completed" : "exception", scopeStart, Stopwatch.GetTimestamp(), width, height);
            }
        }
    }

    private void PublishTraced(byte[] pixels, int width, int height, double renderMs, bool spoutEnabled,
        GapState gapState, SyncAccuracyTrace trace, long sessionId, int generation, long sequence, bool combineBitmapAndSpout)
    {
        double bitmapMs, spoutMs;
        long start = Stopwatch.GetTimestamp();
        bool succeeded = false;
        if (combineBitmapAndSpout && spoutEnabled && _updateDisplayWithSpout != null)
            (bitmapMs, spoutMs) = PublishCombined(pixels, width, height, trace, sessionId, generation, sequence);
        else
        {
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
        }
        // Keep these legacy counters restricted to published frames.
        _recordPerformance(new RenderFramePerformanceMeasurement(renderMs, bitmapMs, spoutMs, width, height, spoutEnabled));
        start = Stopwatch.GetTimestamp(); succeeded = false; bool copied = false;
        try { copied = _copyFreezeFrame(pixels, gapState, width, height); succeeded = true; }
        finally { trace.RecordRenderStage(sessionId, null, generation, sequence, "freeze-copy", !succeeded ? "exception" : copied ? "copied" : "not-needed", start, Stopwatch.GetTimestamp(), width, height); }
        if (_queuePreview != null)
        {
            start = Stopwatch.GetTimestamp();
            bool previewReturned = QueuePreview();
            trace.RecordRenderStage(sessionId, null, generation, sequence, "preview-prepare",
                previewReturned ? "call-returned" : "exception", start, Stopwatch.GetTimestamp(), width, height);
        }
    }
}
