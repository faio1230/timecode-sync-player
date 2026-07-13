using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class RenderFramePerformanceRecorderTests
{
    [Fact]
    public void Record_RecordsRenderedFrameIntoStats()
    {
        var stats = new PlaybackPerformanceStats(TimeSpan.FromSeconds(1));
        stats.RecordTick(0.0, DateTime.UtcNow);
        var recorder = new RenderFramePerformanceRecorder(stats);

        recorder.Record(new RenderFramePerformanceMeasurement(
            RenderMs: 1.2,
            BitmapMs: 2.3,
            SpoutMs: 3.4,
            Width: 320,
            Height: 180,
            SpoutEnabled: true));

        stats.TotalRenderedFrames.Should().Be(1);
    }
}
