using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class RenderFramePublishPipelineTests
{
    [Fact]
    public void Publish_RunsDisplaySpoutPerformanceAndFreezeCopyInOrder()
    {
        var calls = new List<string>();
        var pipeline = new RenderFramePublishPipeline(
            updateDisplay: (width, height) =>
            {
                calls.Add($"display:{width}x{height}");
                return 2.0;
            },
            publishSpout: (pixels, width, height) =>
            {
                calls.Add($"spout:{width}x{height}:{pixels}");
                return 3.0;
            },
            recordPerformance: measurement => calls.Add($"perf:{measurement.RenderMs}:{measurement.BitmapMs}:{measurement.SpoutMs}:{measurement.SpoutEnabled}"),
            copyFreezeFrame: (state, width, height) =>
            {
                calls.Add($"freeze:{state}:{width}x{height}");
                return true;
            });

        pipeline.Publish(
            pixels: new IntPtr(123),
            width: 320,
            height: 180,
            renderMs: 1.0,
            spoutEnabled: true,
            gapState: GapState.WaitingForFrameStep);

        calls.Should().Equal(
            "display:320x180",
            "spout:320x180:123",
            "perf:1:2:3:True",
            "freeze:WaitingForFrameStep:320x180");
    }
}
