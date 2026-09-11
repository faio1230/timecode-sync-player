using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace TimecodeSyncPlayer;

internal sealed class RenderFramePublishPipeline
{
    private readonly Func<OutputFrame, double> _updateDisplay;
    private readonly Func<IntPtr, int, int, double> _publishSpout;
    private readonly Action<RenderFramePerformanceMeasurement> _recordPerformance;
    private readonly Func<byte[], GapState, int, int, bool> _copyFreezeFrame;
    private readonly Action<string>? _queuePreview;
    private readonly Action? _cancelPreview;
    private readonly Func<OutputFrame, Func<double>, (double BitmapMs, double SpoutMs)>? _updateDisplayWithSpout;

    public RenderFramePublishPipeline(
        Func<OutputFrame, double> updateDisplay,
        Func<IntPtr, int, int, double> publishSpout,
        Action<RenderFramePerformanceMeasurement> recordPerformance,
        Func<byte[], GapState, int, int, bool> copyFreezeFrame,
        Action<string>? queuePreview = null, Action? cancelPreview = null,
        Func<OutputFrame, Func<double>, (double BitmapMs, double SpoutMs)>? updateDisplayWithSpout = null)
    {
        _updateDisplay = updateDisplay;
        _publishSpout = publishSpout;
        _recordPerformance = recordPerformance;
        _copyFreezeFrame = copyFreezeFrame;
        _queuePreview = queuePreview;
        _cancelPreview = cancelPreview;
        _updateDisplayWithSpout = updateDisplayWithSpout;
    }

    public void Publish(OutputFrame frame, bool spoutEnabled, GapState gapState = GapState.Inactive,
        SyncAccuracyTrace? trace = null, long sessionId = 0, bool combineBitmapAndSpout = false,
        bool sendSpout = true)
    {
        try
        {
            // Only native frames carry attribution, counters and source-frame capture.
            if (frame.Kind != OutputFrameKind.Normal)
            {
                _updateDisplay(frame);
                if (sendSpout) PublishSpout(frame.PixelArray, frame.Width, frame.Height);
                QueuePreview(frame.DiagnosticKind);
                return;
            }
            if (!sendSpout)
                throw new ArgumentException("Bitmap-only publication is supported only for selected special images.", nameof(sendSpout));
            PublishCore(frame, spoutEnabled, gapState, trace, sessionId, combineBitmapAndSpout);
        }
        catch
        {
            // A failed external update must not leak through an older pending preview.
            try { _cancelPreview?.Invoke(); }
            catch (Exception ex)
            {
                try { Log.Warning(ex, "Preview cancellation failed after external publication failure"); }
                catch (Exception) { }
            }
            throw;
        }
    }

    private void PublishCore(OutputFrame frame, bool spoutEnabled, GapState gapState,
        SyncAccuracyTrace? trace, long sessionId, bool combineBitmapAndSpout)
    {
        if (trace?.IsEnabled == true)
        {
            PublishTraced(frame, spoutEnabled, gapState, trace, sessionId, combineBitmapAndSpout);
            return;
        }
        double bitmapMs, spoutMs;
        if (combineBitmapAndSpout && spoutEnabled && _updateDisplayWithSpout != null)
            (bitmapMs, spoutMs) = PublishCombined(frame, null, sessionId);
        else
        {
            bitmapMs = _updateDisplay(frame);
            spoutMs = PublishSpout(frame.PixelArray, frame.Width, frame.Height);
        }
        _recordPerformance(new RenderFramePerformanceMeasurement(frame.RenderMs!.Value,
            bitmapMs, spoutMs, frame.Width, frame.Height, spoutEnabled));
        _copyFreezeFrame(frame.PixelArray, gapState, frame.Width, frame.Height);
        QueuePreview(frame.DiagnosticKind);
    }

    private bool QueuePreview(string kind)
    {
        try { _queuePreview?.Invoke(kind); return true; }
        catch (Exception ex)
        {
            try { Log.Warning(ex, "Preview callback failed after external frame publication"); }
            catch (Exception) { /* Preview/logging cannot invalidate external publication. */ }
            return false;
        }
    }

    private double PublishSpout(byte[] pixels, int width, int height)
    {
        // The caller owns the OutputFrame until synchronous publication finishes. Spout
        // consumes this CPU pointer during SendFrame; it must not retain it after return.
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { return _publishSpout(pin.AddrOfPinnedObject(), width, height); }
        finally { pin.Free(); }
    }

    private (double BitmapMs, double SpoutMs) PublishCombined(OutputFrame frame, SyncAccuracyTrace? trace, long sessionId)
    {
        byte[] pixels = frame.PixelArray;
        int width = frame.Width, height = frame.Height, generation = frame.Generation!.Value;
        long sequence = frame.Sequence!.Value;
        long scopeStart = trace != null ? Stopwatch.GetTimestamp() : 0;
        long sendStart = 0, sendEnd = 0;
        bool succeeded = false, sent = false;
        try
        {
            using var bitmapScope = trace != null ? new BitmapRenderTraceScope(trace, sessionId, generation, sequence, width, height) : null;
            var result = _updateDisplayWithSpout!(frame, () =>
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

    private void PublishTraced(OutputFrame frame, bool spoutEnabled,
        GapState gapState, SyncAccuracyTrace trace, long sessionId, bool combineBitmapAndSpout)
    {
        byte[] pixels = frame.PixelArray;
        int width = frame.Width, height = frame.Height, generation = frame.Generation!.Value;
        long sequence = frame.Sequence!.Value;
        double renderMs = frame.RenderMs!.Value;
        double bitmapMs, spoutMs;
        long start = Stopwatch.GetTimestamp();
        bool succeeded = false;
        if (combineBitmapAndSpout && spoutEnabled && _updateDisplayWithSpout != null)
            (bitmapMs, spoutMs) = PublishCombined(frame, trace, sessionId);
        else
        {
            try
            {
                using var bitmapScope = new BitmapRenderTraceScope(trace, sessionId, generation, sequence, width, height);
                bitmapMs = _updateDisplay(frame);
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
            bool previewReturned = QueuePreview(frame.DiagnosticKind);
            trace.RecordRenderStage(sessionId, null, generation, sequence, "preview-prepare",
                previewReturned ? "call-returned" : "exception", start, Stopwatch.GetTimestamp(), width, height);
        }
    }
}
