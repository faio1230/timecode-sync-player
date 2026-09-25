using System.IO;
using System.Text;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public sealed class ExternalSpoutReceiverMonitorTests
{
    [Fact]
    public void SummaryParser_ReadsContinuityAndGpuReadbackMetrics()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "{\"event\":\"frame\",\"senderFrame\":10}\n" +
                "{\"event\":\"summary\",\"errors\":0,\"connectedEver\":true," +
                "\"frameCounterAvailable\":true,\"polls\":1000,\"receiveFailures\":2," +
                "\"uniqueFrames\":900,\"observedIntervals\":899,\"counterJumps\":3," +
                "\"missedSenderFrames\":4,\"counterResets\":0,\"disconnects\":0," +
                "\"metadataChanges\":1,\"gapsAtLeast100Ms\":2,\"gapsAtLeast250Ms\":1," +
                "\"gapsAtLeast500Ms\":1,\"maxGapMs\":612.5,\"receiveAtLeast10Ms\":4," +
                "\"receiveAtLeast50Ms\":2,\"receiveAtLeast100Ms\":1,\"maxReceiveMs\":120.25," +
                "\"pixelSamples\":15,\"blackPixelSamples\":1,\"unchangedPixelSamples\":2," +
                "\"contentChanges\":12,\"maxUnchangedSampleStreak\":3,\"maxUnchangedMs\":99.5," +
                "\"nonBlackRunsAtLeast100Ms\":7,\"nonBlackRunsAtLeast250Ms\":3," +
                "\"nonBlackRunsAtLeast500Ms\":1,\"maxNonBlackUnchangedMs\":612.75," +
                "\"maxNonBlackUnchangedQpc\":123456789," +
                "\"readbackAtLeast10Ms\":3,\"readbackAtLeast50Ms\":1," +
                "\"readbackAtLeast100Ms\":0,\"maxGpuReadbackMs\":55.5," +
                "\"wallSeconds\":15.2,\"cpuSeconds\":0.8}\n",
                new UTF8Encoding(false));

            ExternalSpoutReceiverSummary summary =
                ExternalSpoutReceiverSummaryParser.Parse(path, exitCode: 0, timedOut: false);

            summary.SummaryFound.Should().BeTrue();
            summary.CompletedNormally.Should().BeTrue();
            summary.UniqueFrames.Should().Be(900);
            summary.MissedSenderFrames.Should().Be(4);
            summary.GapsAtLeast500Ms.Should().Be(1);
            summary.MaxGapMilliseconds.Should().Be(612.5);
            summary.MaxReceiveMilliseconds.Should().Be(120.25);
            summary.MaxGpuReadbackMilliseconds.Should().Be(55.5);
            summary.ContentChanges.Should().Be(12);
            summary.MaxUnchangedMilliseconds.Should().Be(99.5);
            summary.NonBlackRunsAtLeast500Ms.Should().Be(1);
            summary.MaxNonBlackUnchangedMilliseconds.Should().Be(612.75);
            summary.MaxNonBlackUnchangedQpc.Should().Be(123456789);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SummaryParser_RejectsTruncatedRunWithoutSummary()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"event\":\"frame\"}\n{\"event\":", new UTF8Encoding(false));

            ExternalSpoutReceiverSummary summary =
                ExternalSpoutReceiverSummaryParser.Parse(path, exitCode: 1, timedOut: true);

            summary.SummaryFound.Should().BeFalse();
            summary.CompletedNormally.Should().BeFalse();
            summary.TimedOut.Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
