using System.Diagnostics;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// U1: ギャップ動作の切替で出る age 範囲外警告が、LTC フレームの処理遅れではなく
/// ReapplyLastAcceptedFrame（信号停止前の FrameEndTimestamp を使う再適用）で出ることと、
/// その再適用が生の LTC 秒（age をクランプして 0 加算）を目標に使うことを固定する。
/// 実機を使わず、QPC 注入で停止 1.5 秒を決定的に再現する。
/// </summary>
[Collection("Serilog global logger")]
public sealed class LtcReapplyAgeTests
{
    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    [Fact]
    public void GapBehaviorReapply_LogsStaleAgeAsReapply_AndUsesRawSeconds()
    {
        long qpc = 1_000_000_000;
        var sink = new ListSink();
        ILogger previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        try
        {
            var harness = new SyncScenarioHarness(sampleClockEnabled: true, getQpc: () => qpc);
            harness.AddTrack("clip-a", timelineIn: 0, duration: 60);
            harness.ChangeMode(SyncMode.Single);
            harness.AdvancePlayback(1.0);

            // 信号がある状態で最後のフレームを受ける（age 0）。その後 Black へ切替。
            harness.SupplyLtcFrame(10.0, frameEndTimestamp: qpc);
            harness.GapBehavior = GapBehavior.Black;

            // 信号停止を模して 1.5 秒進めてから Freeze へ切替（再適用が古い FrameEndTimestamp を使う）。
            qpc += (long)(Stopwatch.Frequency * 1.5);
            harness.GapBehavior = GapBehavior.Freeze;

            List<LogEvent> events;
            lock (sink.Events) events = sink.Events.ToList();

            LogEvent warning = events.Should().ContainSingle(e =>
                e.MessageTemplate.Text.Contains("LTC sample clock") &&
                e.MessageTemplate.Text.Contains("範囲外")).Which;
            warning.Properties["Source"].Should().Be(new ScalarValue("reapply"));
            ((double)((ScalarValue)warning.Properties["AgeMs"]).Value!)
                .Should().BeApproximately(1500.0, 5.0);

            LogEvent applied = events.Last(e => e.MessageTemplate.Text.StartsWith("sync.apply"));
            events.IndexOf(applied).Should().BeGreaterThan(events.IndexOf(warning),
                "再適用の sync.apply が警告（age クランプ）の後に記録される");
            ((double)((ScalarValue)applied.Properties["Ltc"]).Value!)
                .Should().BeApproximately(10.0, 0.001);
        }
        finally
        {
            Log.Logger = previous;
        }
    }
}
