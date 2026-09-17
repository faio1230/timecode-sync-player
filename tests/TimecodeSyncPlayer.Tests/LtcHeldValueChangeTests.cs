using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D31-b: 保持損失中に保持値（タイムコード停止位置）が変わったとき、停止モードは新しい
/// 保持値へ 1 回だけ着地する（D27 の着地を遷移時から変化時へ拡張）。ランスルーは同じ変化の
/// 判定で同期を 1 回適用する。同じ保持値の連続では発行しない。
/// </summary>
[Collection("Serilog global logger")]
public sealed class LtcHeldValueChangeTests
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
    private static (SyncScenarioHarness Harness, ManualTimeProvider Clock) Arrange(LtcSignalLossMode mode)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true)
        {
            SignalLossMode = mode,
            GapBehavior = GapBehavior.Black,
        };
        h.AddTrack("A", 0, 30);
        h.ReloadProject();
        h.SetDurationSeconds(30);
        h.ManualPlay();
        // 初期位置を保持値に合わせ、最初の有効フレームでシーク保留を作らない（保留セトルが
        // 変化フレームのシークを抑止しないようにする）。
        h.AdvancePlayback(20.0);
        return (h, clock);
    }

    private static bool IsApplyOnceWithReason(LogEvent logEvent, string reason) =>
        logEvent.MessageTemplate.Text.Contains("applying the") &&
        logEvent.Properties.TryGetValue("Reason", out LogEventPropertyValue? value) &&
        value is ScalarValue scalar && scalar.Value?.ToString() == reason;

    /// <summary>保持フレームを供給しつつ有効フレームの時計を進め、timeout（250ms）超えで損失を確定させる。</summary>
    private static void HoldPastTimeout(SyncScenarioHarness h, double heldSeconds)
    {
        h.SupplyHeldLtc(heldSeconds);
        h.Tick100Milliseconds();
        h.SupplyHeldLtc(heldSeconds);
        h.Tick100Milliseconds();
        h.SupplyHeldLtc(heldSeconds);
    }

    private static IReadOnlyList<double> SeekTargets(SyncScenarioHarness h) =>
        h.Operations.Where(o => o.Name == "seek").Select(o => o.Value ?? double.NaN).ToList();

    [Fact]
    public void StoppedLoss_HeldValueChange_LandsOnceOnTheNewValue()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = Arrange(LtcSignalLossMode.Stop);
        h.SupplyLtc(20.0);
        h.Operations.Clear();

        HoldPastTimeout(h, 20.0);
        h.Tick100Milliseconds();

        h.IsPaused.Should().BeTrue("保持の損失で停止する");
        SeekTargets(h).Should().Equal(new[] { 20.0 }, "遷移時の着地は保持値へ 1 回発行する");

        h.Operations.Clear();
        clock.Advance(TimeSpan.FromMilliseconds(600));

        h.SupplyHeldLtc(8.0);
        SeekTargets(h).Should().Equal(new[] { 8.0 }, "保持値が変わったら新しい値へ 1 回だけ着地する");

        h.SupplyHeldLtc(8.0);
        h.SupplyHeldLtc(8.0);
        SeekTargets(h).Should().Equal(new[] { 8.0 }, "同じ保持値の連続では着地しない");
    }

    [Fact]
    public void StoppedLoss_SameHeldValueRepeats_DoNotLandAgain()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = Arrange(LtcSignalLossMode.Stop);
        h.SupplyLtc(20.0);
        h.Operations.Clear();

        HoldPastTimeout(h, 20.0);
        h.Tick100Milliseconds();
        SeekTargets(h).Should().Equal(20.0);

        h.Operations.Clear();
        clock.Advance(TimeSpan.FromMilliseconds(600));

        h.SupplyHeldLtc(20.0);
        h.SupplyHeldLtc(20.0);
        SeekTargets(h).Should().BeEmpty("同じ保持値の連続では着地を繰り返さない");
    }

    [Fact]
    public void RunThrough_HeldValueChange_AppliesOnceWithoutPausing()
    {
        using var capture = new LoggerCapture(new ListSink());
        (SyncScenarioHarness h, ManualTimeProvider clock) = Arrange(LtcSignalLossMode.RunThrough);
        h.SupplyLtc(20.0);
        h.Operations.Clear();

        HoldPastTimeout(h, 20.0);
        h.Tick100Milliseconds();

        h.IsPaused.Should().BeFalse("ランスルーは保持でも停止しない");
        h.Operations.Should().NotContain(o => o.Name == "signal-loss-pause");

        h.Operations.Clear();
        // ファイルロードの安定待ち（1 秒）を明けて、変化フレームの同期要求がシークまで届くようにする。
        clock.Advance(TimeSpan.FromSeconds(1.2));

        h.SupplyHeldLtc(8.0);
        List<LogEvent> events = capture.Snapshot();
        events.Count(e => IsApplyOnceWithReason(e, "held value change"))
            .Should().Be(1, "保持値の変化で 1 回だけ適用する");
        SeekTargets(h).Should().Equal(new[] { 8.0 }, "新しい保持値へシークする");

        h.SupplyHeldLtc(8.0);
        SeekTargets(h).Should().Equal(new[] { 8.0 }, "同じ保持値の連続では適用しない");
    }
}
