using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.3 D38: 保持後のジャンプの着地の遅れ（`docs/design/v0.5.3-d38-seek-gates.md` §6）。
/// (a) 同期を適用しないフレーム（保持の Duplicate）でも、保留の着地を観測して位置の信頼を戻す。
/// (b) 位置が未信頼でも、pending から 4×tolerance を超える新しい要求は置き換える。
/// 門 3: `JumpAppliedOnce` で Jump を捨てたときは Information を 1 行出す（振る舞いは変えない）。
/// </summary>
[Collection("Serilog global logger")]
public sealed class D38SeekGatesTests
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

    private static bool IsDroppedJumpLog(LogEvent logEvent) =>
        logEvent.MessageTemplate.Text.Contains("Jump dropped (JumpAppliedOnce)", StringComparison.Ordinal);

    private static void Raw(SyncScenarioHarness h, double seconds, long at, double detectedFps = 25.0)
    {
        int frame = (int)Math.Round(seconds * 25.0);
        var timecode = new LtcTimecode(
            frame / (25 * 3600), (frame / (25 * 60)) % 60, (frame / 25) % 60, frame % 25, false);
        h.Controller.ReceiveFrame(new LtcFrameReceivedEventArgs(timecode, detectedFps, seconds, 0, 0), at);
    }

    private static void Tick(SyncScenarioHarness h, ManualTimeProvider clock, int count)
    {
        for (int i = 0; i < count; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            h.Tick100Milliseconds();
        }
    }

    [Fact]
    public void HeldDuplicate_WhenPlaybackReachesPendingTarget_SettlesAndRestoresPositionTrust()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(10.0);

        // 着地シークを発行した状態（保留 + 位置は未信頼）。
        h.SyncService.ReportSeekSent(10.0);
        h.SyncService.SeekState.HasPendingSeek.Should().BeTrue("前提: シークの保留がある");
        h.SyncService.IsPlaybackPositionUsable.Should().BeFalse("前提: シーク中は位置を信頼しない");

        // 保持の Duplicate。位置は既に着地の窓（10.0±tol）の中。200ms の cooldown を跨ぐ。
        h.SupplyHeldLtc(5.0);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        h.SupplyHeldLtc(5.0);

        h.SyncService.SeekState.HasPendingSeek.Should().BeFalse(
            "保持の Duplicate でも着地を観測して Settled になる（D38 (a)）");
        h.SyncService.IsPlaybackPositionUsable.Should().BeTrue(
            "Settled で位置の信頼が戻る（D38 (a)）");
    }

    [Fact]
    public void Untrusted_FarNewRequest_DiscardsUnreachablePendingWithoutWaitingForTimeout()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true);
        h.AddTrack("A", 0, 30);
        h.ReloadProject();
        h.SetDurationSeconds(30);
        h.ManualPlay();
        h.AdvancePlayback(5.0);

        // 保留の目標 10.0（再生位置 5.0 からは届かない）。位置は未信頼。
        h.SyncService.ReportSeekSent(10.0);
        h.SyncService.IsPlaybackPositionUsable.Should().BeFalse("前提: シーク中は位置を信頼しない");

        // 未信頼のまま、pending の目標から 4×tolerance を超える要求（LTC 20.0 → 目標 20.0）。
        h.SupplyLtc(20.0);

        h.SyncService.SeekState.HasPendingSeek.Should().BeFalse(
            "未信頼でも 4×tolerance を超える新しい要求は、到達不能な pending を捨てる（D38 (b)）");
        h.SyncService.IsPlaybackPositionUsable.Should().BeFalse(
            "捨てた後は位置の再確認（11）とゲート（13）を通ってからシークする");
    }

    [Fact]
    public void JumpDroppedByJumpAppliedOnce_LogsOneLine()
    {
        using var capture = new LoggerCapture(new ListSink());
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock) { SignalLossMode = LtcSignalLossMode.Stop, GapBehavior = GapBehavior.Black };
        h.AddTrack("A", 5, 20);
        h.ReloadProject();
        h.ManualPlay();
        Raw(h, 7.0, 10_000);
        Raw(h, 7.04, 10_040);
        Tick(h, clock, 3);
        h.IsPaused.Should().BeTrue("前提: 無音の信号断で一時停止する");

        Raw(h, 12.0, 10_340);   // 1 枚目: 損失中なので 1 回だけ適用され、JumpAppliedOnce が立つ
        h.Controller.SignalLossLatchSnapshot()["lost"].Should().BeTrue("前提: 無音の損失は Jump 1 枚では解けない");
        capture.Snapshot().Count(IsDroppedJumpLog).Should().Be(0, "前提: まだ捨てていない");

        Tick(h, clock, 3);
        Raw(h, 16.0, 10_640);   // 2 枚目: JumpAppliedOnce が残っているため捨てられる

        capture.Snapshot().Count(IsDroppedJumpLog).Should().Be(1,
            "JumpAppliedOnce で捨てたときは Information を 1 行出す（門 3。振る舞いは変えない）");
    }
}
