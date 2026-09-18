using System.Diagnostics;
using System.IO;
using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// 0.4.5-A フェーズ 1: 評価位置（shadow）は trace に併記するだけで、
/// playback= / delta= の意味は変えない。位置未信頼のフレームでも shadow を残す。
/// OutputTrace.Current を差し替えるため直列コレクション。
/// </summary>
[Collection("OutputTrace")]
public class PositionShadowTraceTests
{
    [Fact]
    public void EvaluateDecision_AppendsShadowFields_WithoutChangingPlayback()
    {
        var trace = new OutputTrace(CreateTempDirectory(), capacity: 1000) { OriginQpc = Stopwatch.GetTimestamp() };
        OutputTrace.Current = trace;
        try
        {
            var service = new TimecodeSyncService(new SyncDecisionEngine(), new TimecodeSyncSeekState());
            var state = new SyncPlaybackState(true, true, false, PlaybackSeconds: 10.0,
                DurationSeconds: 60, VideoFps: 25, TimecodeFps: 25);
            var sample = new PlaybackPositionSample(10.5, PlaybackPositionBasis.Pipeline, 3, 10.5, 3, 3);

            _ = service.EvaluateDecision(10.5, state, sample);
        }
        finally
        {
            OutputTrace.Current = OutputTrace.Disabled;
        }

        OutputTraceEvent recorded = trace.Snapshot().Single(e => e.Stage == "sync.evaluate");
        recorded.Detail.Should().Contain("playback=10.000000");
        recorded.Detail.Should().Contain("evalPosition=10.500000");
        recorded.Detail.Should().Contain("evalDelta=0.000000");
        recorded.Detail.Should().Contain("evalBasis=pipeline");
        recorded.Detail.Should().Contain("deliveredGen=3");
        recorded.Detail.Should().Contain("currentGen=3");
        recorded.Detail.Should().Contain("shadowRate=1.00000");
        recorded.Detail.Should().Contain("shadowRateReason=smooth-idle");
    }

    [Fact]
    public void UntrustedFrame_StillRecordsShadow()
    {
        var trace = new OutputTrace(CreateTempDirectory(), capacity: 1000) { OriginQpc = Stopwatch.GetTimestamp() };
        OutputTrace.Current = trace;
        try
        {
            var service = new TimecodeSyncService(new SyncDecisionEngine(), new TimecodeSyncSeekState());
            service.ReportSeekSent(10.0);
            var state = new SyncPlaybackState(true, true, false, PlaybackSeconds: 10.0,
                DurationSeconds: 60, VideoFps: 25, TimecodeFps: 25);
            var sample = new PlaybackPositionSample(10.5, PlaybackPositionBasis.Delivered, 4, 10.2, 3, 4);

            _ = service.EvaluateDecision(10.5, state, sample);
        }
        finally
        {
            OutputTrace.Current = OutputTrace.Disabled;
        }

        OutputTraceEvent recorded = trace.Snapshot().Single(e => e.Stage == "sync.evaluate");
        recorded.Detail.Should().Contain("reason=position-untrusted");
        recorded.Detail.Should().Contain("playback=10.000000");
        recorded.Detail.Should().Contain("evalPosition=");
        recorded.Detail.Should().Contain("evalBasis=delivered");
        recorded.Detail.Should().Contain("deliveredGen=3");
        recorded.Detail.Should().Contain("currentGen=4");
        recorded.Detail.Should().Contain("shadowRate=1.10000");
        recorded.Detail.Should().Contain("shadowRateReason=smooth");
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tcs-shadow-trace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
