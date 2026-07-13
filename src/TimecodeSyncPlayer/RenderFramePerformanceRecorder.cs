namespace TimecodeSyncPlayer;

public sealed record RenderFramePerformanceMeasurement(
    double RenderMs,
    double BitmapMs,
    double SpoutMs,
    int Width,
    int Height,
    bool SpoutEnabled);

public sealed class RenderFramePerformanceRecorder
{
    private readonly PlaybackPerformanceStats _stats;

    public RenderFramePerformanceRecorder(PlaybackPerformanceStats stats)
    {
        _stats = stats;
    }

    public void Record(RenderFramePerformanceMeasurement measurement)
    {
        _stats.RecordRenderedFrame(
            measurement.RenderMs,
            measurement.BitmapMs,
            measurement.SpoutMs,
            measurement.Width,
            measurement.Height,
            measurement.SpoutEnabled);
    }
}
