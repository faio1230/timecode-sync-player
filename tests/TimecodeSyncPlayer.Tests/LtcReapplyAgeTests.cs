using System.Diagnostics;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// U1: ギャップ動作の再適用（GapBehaviorChanged 等）で、最後のフレーム終端から 0.5 秒より
/// 古いときは同期要求（シーク目標）を出さず、次の有効フレームに任せる。ギャップ表示の
/// 切替は従来どおり即時。再適用は frame/tick 用の一度きり age 警告を消費しない。
/// 実機を使わず、QPC 注入で信号停止を決定的に再現する。
/// </summary>
[Collection("Serilog global logger")]
public sealed class LtcReapplyAgeTests
{
    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    private sealed class LoggerCapture : IDisposable
    {
        private readonly ILogger _previous;
        public LoggerCapture(ListSink sink)
        {
            Sink = sink;
            _previous = Log.Logger;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        }

        public ListSink Sink { get; }

        public List<LogEvent> Snapshot()
        {
            lock (Sink.Events) return Sink.Events.ToList();
        }

        public void Dispose() => Log.Logger = _previous;
    }

    private static LoggerCapture CaptureLogger() => new(new ListSink());

    private static List<LogEvent> AgeWarnings(IEnumerable<LogEvent> events) =>
        events.Where(e => e.MessageTemplate.Text.Contains("LTC sample clock") &&
            e.MessageTemplate.Text.Contains("範囲外")).ToList();

    private static int SyncApplyCount(IEnumerable<LogEvent> events) =>
        events.Count(e => e.MessageTemplate.Text.StartsWith("sync.apply"));

    [Fact]
    public void StaleReapply_DefersSyncButAppliesGapDisplayImmediately()
    {
        long qpc = 1_000_000_000;
        using LoggerCapture capture = CaptureLogger();
        var harness = new SyncScenarioHarness(sampleClockEnabled: true, getQpc: () => qpc);
        harness.AddTrack("clip-a", timelineIn: 0, duration: 5);
        harness.GapBehavior = GapBehavior.Black;
        harness.AdvancePlayback(1.0);
        harness.SupplyLtcFrame(7.0, frameEndTimestamp: qpc);
        harness.Operations.Clear();

        qpc += (long)(Stopwatch.Frequency * 1.5);
        harness.GapBehavior = GapBehavior.Freeze;

        // 表示切替は即時: 前トラック最終フレーム（5 秒 - 1/25 秒 = 4.96）が対象。
        harness.Operations.Should().Contain(op =>
            (op.Name == "seek" || op.Name == "load-paused") &&
            op.Value.HasValue && Math.Abs(op.Value.Value - 4.96) < 0.05);
        // 古い LTC 秒（7.0）への同期要求（シーク目標）は出ない。
        harness.Operations.Should().NotContain(op =>
            op.Value.HasValue && Math.Abs(op.Value.Value - 7.0) < 0.2);
        AgeWarnings(capture.Snapshot()).Should().BeEmpty("再適用は age 警告を出さない");

        // 次の有効フレーム（1 フレーム進んだ Normal）で同期が適用される。
        int before = SyncApplyCount(capture.Snapshot());
        harness.SupplyLtcFrame(7.04, frameEndTimestamp: qpc);
        SyncApplyCount(capture.Snapshot()).Should().BeGreaterThan(before);
    }

    [Fact]
    public void FreshReapply_StillRequestsSync()
    {
        long qpc = 1_000_000_000;
        using LoggerCapture capture = CaptureLogger();
        var harness = new SyncScenarioHarness(sampleClockEnabled: true, getQpc: () => qpc);
        harness.AddTrack("clip-a", timelineIn: 0, duration: 60);
        harness.ChangeMode(SyncMode.Single);
        harness.AdvancePlayback(1.0);
        harness.SupplyLtcFrame(10.0, frameEndTimestamp: qpc);
        int before = SyncApplyCount(capture.Snapshot());

        // 0.5 秒以内の再適用は従来どおり同期要求を出す。
        qpc += Stopwatch.Frequency / 5;
        harness.GapBehavior = GapBehavior.Black;

        SyncApplyCount(capture.Snapshot()).Should().BeGreaterThan(before);
    }

    [Fact]
    public void SampleClockOff_ReapplyIgnoresStaleness()
    {
        long qpc = 1_000_000_000;
        using LoggerCapture capture = CaptureLogger();
        var harness = new SyncScenarioHarness(sampleClockEnabled: false, getQpc: () => qpc);
        harness.AddTrack("clip-a", timelineIn: 0, duration: 60);
        harness.ChangeMode(SyncMode.Single);
        harness.AdvancePlayback(1.0);
        harness.SupplyLtcFrame(10.0, frameEndTimestamp: qpc);
        int before = SyncApplyCount(capture.Snapshot());

        // off では age を使わないため、経過時間によらず再適用は同期要求を出す。
        qpc += (long)(Stopwatch.Frequency * 1.5);
        harness.GapBehavior = GapBehavior.Black;

        SyncApplyCount(capture.Snapshot()).Should().BeGreaterThan(before);
    }

    [Fact]
    public void StaleReapply_DoesNotConsumeFramePathAgeWarning()
    {
        long qpc = 1_000_000_000;
        using LoggerCapture capture = CaptureLogger();
        var harness = new SyncScenarioHarness(sampleClockEnabled: true, getQpc: () => qpc);
        harness.AddTrack("clip-a", timelineIn: 0, duration: 60);
        harness.ChangeMode(SyncMode.Single);
        harness.AdvancePlayback(1.0);
        harness.SupplyLtcFrame(10.0, frameEndTimestamp: qpc);

        qpc += (long)(Stopwatch.Frequency * 1.5);
        harness.GapBehavior = GapBehavior.Black;
        AgeWarnings(capture.Snapshot()).Should().BeEmpty();

        // フレーム経路の古い age は従来どおり 1 回警告される（再適用が消費していない）。
        harness.SupplyLtcFrame(10.04, frameEndTimestamp: qpc - (long)(Stopwatch.Frequency * 1.5));

        List<LogEvent> warnings = AgeWarnings(capture.Snapshot());
        warnings.Should().ContainSingle();
        warnings[0].Properties["Source"].Should().Be(new ScalarValue("frame"));
    }
}
