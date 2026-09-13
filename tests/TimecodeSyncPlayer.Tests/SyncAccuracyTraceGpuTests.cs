using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer;

namespace TimecodeSyncPlayer.Tests;

public class SyncAccuracyTraceGpuTests
{
    [Fact]
    public void RecordGpuFrame_WritesFrameEventWithMarker()
    {
        string path = Path.Combine(Path.GetTempPath(), "tcs-accuracy-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var trace = SyncAccuracyTrace.Create(path);
            trace.IsEnabled.Should().BeTrue();

            trace.RecordGpuFrame("gpu", 1920, 1080, new AccuracyFrameProbe(true, 2, 1234, false),
                publishedTicks: 111, probeTicks: 7);
            trace.Dispose();

            string[] lines = File.ReadAllLines(path);
            lines.Should().Contain(l => l.Contains("\"type\":\"frame\"", StringComparison.Ordinal)
                && l.Contains("\"frameIndex\":1234", StringComparison.Ordinal)
                && l.Contains("\"ticks\":111", StringComparison.Ordinal)
                && l.Contains("\"kind\":\"gpu\"", StringComparison.Ordinal));
        }
        finally
        {
            try { File.Delete(path); } catch { /* 検証用一時なので失敗は無視 */ }
        }
    }
}
