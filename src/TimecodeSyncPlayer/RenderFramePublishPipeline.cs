namespace TimecodeSyncPlayer;

internal sealed class RenderFramePublishPipeline
{
    private readonly Func<int, int, double> _updateDisplay;
    private readonly Func<IntPtr, int, int, double> _publishSpout;
    private readonly Action<RenderFramePerformanceMeasurement> _recordPerformance;
    private readonly Func<GapState, int, int, bool> _copyFreezeFrame;

    public RenderFramePublishPipeline(
        Func<int, int, double> updateDisplay,
        Func<IntPtr, int, int, double> publishSpout,
        Action<RenderFramePerformanceMeasurement> recordPerformance,
        Func<GapState, int, int, bool> copyFreezeFrame)
    {
        _updateDisplay = updateDisplay;
        _publishSpout = publishSpout;
        _recordPerformance = recordPerformance;
        _copyFreezeFrame = copyFreezeFrame;
    }

    public void Publish(
        IntPtr pixels,
        int width,
        int height,
        double renderMs,
        bool spoutEnabled,
        GapState gapState)
    {
        double bitmapMs = _updateDisplay(width, height);
        double spoutMs = _publishSpout(pixels, width, height);
        _recordPerformance(new RenderFramePerformanceMeasurement(
            renderMs,
            bitmapMs,
            spoutMs,
            width,
            height,
            spoutEnabled));
        _copyFreezeFrame(gapState, width, height);
    }
}
