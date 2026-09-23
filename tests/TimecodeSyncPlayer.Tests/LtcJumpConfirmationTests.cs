using System.Diagnostics;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D30: 誤デコードの単発 Jump をそのまま適用しない。写像がギャップ（先頭オフセットを含む）か
/// 現在と別トラックになる Jump は、次の 1 フレームで値の連続（同値の Duplicate または +1 フレーム）
/// を確認してから適用する。v0.5.1 から同一トラック内の Jump も同じく確認する（保持損失中の同一トラック内
/// の Jump だけは D27-b/c のとおり 1 枚で復帰する）。Fixed fps モードでデコーダ
/// 推定 fps が解決 fps と食い違う Jump も未確認扱い。保持損失からの復帰も確認済み Jump に限る。
/// D31: 確認窓の時計はサンプル時計（FrameEndTimestamp）を優先する。
/// </summary>
[Collection("Serilog global logger")]
public sealed class LtcJumpConfirmationTests
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

    private static bool IsApplyOnceWithReason(LogEvent logEvent, string reason) =>
        logEvent.MessageTemplate.Text.Contains("applying the") &&
        logEvent.Properties.TryGetValue("Reason", out LogEventPropertyValue? value) &&
        value is ScalarValue scalar && scalar.Value?.ToString() == reason;
    private static void Raw(SyncScenarioHarness h, double seconds, long at, double detectedFps = 25.0)
    {
        int frame = (int)Math.Round(seconds * 25.0);
        var timecode = new LtcTimecode(
            frame / (25 * 3600), (frame / (25 * 60)) % 60, (frame / 25) % 60, frame % 25, false);
        h.Controller.ReceiveFrame(new LtcFrameReceivedEventArgs(timecode, detectedFps, seconds, 0, 0), at);
    }

    /// <summary>
    /// A（タイムライン 5〜25）をロード済みで 7.0 まで通常進行している状態。
    /// 同期シークのデバウンス（250ms）とセットル後の抑止（500ms）を明けておき、
    /// 次の Jump が適用されれば必ずシーク・ロードが出るようにする。
    /// </summary>
    private static (SyncScenarioHarness Harness, PlaylistTrack A, PlaylistTrack B, ManualTimeProvider Clock)
        ArrangeContinueWithA()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock) { GapBehavior = GapBehavior.Black };
        PlaylistTrack a = h.AddTrack("A", 5, 20);
        PlaylistTrack b = h.AddTrack("B", 30, 20);
        h.ReloadProject();
        h.ManualPlay();
        Raw(h, 7.0, 10_000);
        Raw(h, 7.04, 10_040);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Raw(h, 7.08, 10_080);
        clock.Advance(TimeSpan.FromMilliseconds(600));
        h.Operations.Clear();
        return (h, a, b, clock);
    }

    private const long SampleClockBase = 100_000_000;
    private static long FrameTicks => Stopwatch.Frequency / 25;

    /// <summary>
    /// D31: サンプル時計（FrameEndTimestamp）付きのフレームで同じ状態を作る。
    /// 壁時計（受信時刻）は Tick100Milliseconds で進めても、サンプル時計は 1 フレームずつ進む。
    /// </summary>
    private static (SyncScenarioHarness Harness, PlaylistTrack A, PlaylistTrack B, ManualTimeProvider Clock)
        ArrangeContinueWithAOnSampleClock()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, sampleClockEnabled: false) { GapBehavior = GapBehavior.Black };
        PlaylistTrack a = h.AddTrack("A", 5, 20);
        PlaylistTrack b = h.AddTrack("B", 30, 20);
        h.ReloadProject();
        h.ManualPlay();
        h.SupplyLtcFrame(7.0, SampleClockBase);
        h.SupplyLtcFrame(7.04, SampleClockBase + FrameTicks);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        h.SupplyLtcFrame(7.08, SampleClockBase + FrameTicks * 2);
        clock.Advance(TimeSpan.FromMilliseconds(600));
        h.Operations.Clear();
        return (h, a, b, clock);
    }

    [Fact]
    public void GapMappingJump_IsNotAppliedUntilTheNextFrameConfirms()
    {
        (SyncScenarioHarness h, _, _, _) = ArrangeContinueWithA();

        Raw(h, 0.04, 10_080);

        h.Operations.Should().NotContain(o => o.Name == "seek" || o.Name == "loadfile" || o.Name == "pause-for-gap");
        h.IsGapActive.Should().BeFalse("未確認の Jump ではギャップへ入らない");
        h.RenderSurface.Should().Be(ScenarioRenderSurface.Video);

        Raw(h, 0.08, 10_120);

        h.IsGapActive.Should().BeTrue("+1 フレームの連続で確認できた Jump は適用する");
        h.RenderSurface.Should().Be(ScenarioRenderSurface.Black);
    }

    [Fact]
    public void PendingJumpConfirmation_UsesTheSampleClockWhenTheWallClockIsLate()
    {
        using var capture = new LoggerCapture(new ListSink());
        (SyncScenarioHarness h, _, _, _) = ArrangeContinueWithAOnSampleClock();

        // ギャップ写像の Jump。サンプル時計は 1 フレーム差だが、コールバックが滞り
        // 壁時計では 200ms 離れている（25fps の確認窓 100ms の外）。
        h.SupplyLtcFrame(0.04, SampleClockBase + FrameTicks * 3);
        h.Tick100Milliseconds(2);
        h.SupplyLtcFrame(0.04, SampleClockBase + FrameTicks * 4);

        List<LogEvent> events = capture.Snapshot();
        events.Should().Contain(e => e.MessageTemplate.Text.Contains("applying the confirmed Jump frame once"),
            "サンプル時計が 1 フレーム差なので確認成立する");
        events.Should().NotContain(e => e.MessageTemplate.Text.Contains("dropping out-of-window pending Jump frame"));
        events.Should().NotContain(e => IsApplyOnceWithReason(e, "held value change"),
            "確認済み Jump は保持値の変更より先に適用する");
        h.IsGapActive.Should().BeTrue();
        h.RenderSurface.Should().Be(ScenarioRenderSurface.Black);
    }

    [Fact]
    public void PendingJumpConfirmation_RejectsAFrameAfterASampleClockGap()
    {
        using var capture = new LoggerCapture(new ListSink());
        (SyncScenarioHarness h, _, _, _) = ArrangeContinueWithAOnSampleClock();

        // 壁時計は 100ms（25fps の確認窓の上限ちょうど）だが、サンプル時計は 600ms 空いている。
        h.SupplyLtcFrame(0.04, SampleClockBase + FrameTicks * 3);
        h.Tick100Milliseconds();
        h.SupplyLtcFrame(0.04, SampleClockBase + FrameTicks * 3 + (long)(Stopwatch.Frequency * 0.6));

        List<LogEvent> events = capture.Snapshot();
        LogEvent drop = events.Last(e => e.MessageTemplate.Text.Contains("dropping out-of-window pending Jump frame"));
        Convert.ToDouble(((ScalarValue)drop.Properties["StreamMs"]).Value).Should().BeApproximately(600.0, 1.0);
        Convert.ToDouble(((ScalarValue)drop.Properties["WallMs"]).Value).Should().BeApproximately(100.0, 1.0);
        events.Should().NotContain(e => e.MessageTemplate.Text.Contains("applying the confirmed Jump frame once"),
            "サンプル時計が 600ms 空いた保留は確認に使わない");
        events.Should().Contain(e => IsApplyOnceWithReason(e, "held value change"),
            "拒否された次のフレームは既存の保持値の変更として処理される");
    }

    [Fact]
    public void GapMappingJump_SameValueDuplicateConfirms()
    {
        (SyncScenarioHarness h, _, _, _) = ArrangeContinueWithA();

        Raw(h, 0.04, 10_080);
        h.IsGapActive.Should().BeFalse();

        Raw(h, 0.04, 10_120);

        h.IsGapActive.Should().BeTrue("同値の Duplicate も確認として扱う");
        h.RenderSurface.Should().Be(ScenarioRenderSurface.Black);
    }

    [Fact]
    public void OtherTrackJump_IsDeferredThenAppliedOnContinuation()
    {
        (SyncScenarioHarness h, _, PlaylistTrack b, _) = ArrangeContinueWithA();

        Raw(h, 40.0, 10_080);

        h.Operations.Should().NotContain(o => o.Name == "loadfile" || o.Name == "seek", "未確認の Jump では切り替えない");
        h.LoadedTrackId.Should().NotBe(b.Id);

        Raw(h, 40.04, 10_120);

        h.Operations.Should().Contain(o => o.Name == "loadfile", "確認できた Jump は適用する");
        h.LoadedTrackId.Should().Be(b.Id);
    }

    [Fact]
    public void SameTrackJump_IsAppliedAfterTheDecisionWindow()
    {
        // D37-a: 同一トラック内の Jump は確認を待たないが、粗い判定のゲートは窓（3 サンプル）が
        // 埋まるまで Seek を保留する。実素材では次の LTC フレーム（40ms 間隔）で埋まる。
        (SyncScenarioHarness h, _, _, _) = ArrangeContinueWithA();

        Raw(h, 12.0, 10_080);
        h.Operations.Should().NotContain(o => o.Name == "seek", "1 サンプル目では出さない");

        Raw(h, 12.04, 10_120);
        Raw(h, 12.08, 10_160);
        Raw(h, 12.12, 10_200);

        h.Operations.Should().Contain(o => o.Name == "seek", "窓が埋まれば同一トラック内の Jump を適用する");
        h.IsGapActive.Should().BeFalse();
    }

    [Fact]
    public void SameTrackJump_SingleCorruptFrame_IsNotApplied()
    {
        // v0.5.1: 検証機の 2 時間試験で、化けた 1 枚（+2.8 秒）を「最初の Jump」として採り、
        // +2.3 秒シークして 0.8 秒後に戻していた。同じトラック内でも次のフレームで確かめる。
        using var capture = new LoggerCapture(new ListSink());
        (SyncScenarioHarness h, _, _, _) = ArrangeContinueWithA();

        Raw(h, 9.88, 10_120);          // 化けた 1 枚（同じトラック内、+2.8 秒）
        Raw(h, 7.12, 10_160);          // 元の流れに戻る
        Raw(h, 7.16, 10_200);
        Raw(h, 7.20, 10_240);
        Raw(h, 7.24, 10_280);

        h.Operations.Should().NotContain(o => o.Name == "seek", "確かめられなかった Jump では動かない");
        capture.Snapshot().Should().Contain(e => e.MessageTemplate.Text.Contains("holding unconfirmed Jump frame"));
    }

    [Fact]
    public void FixedModeJumpWithMismatchedDetectedFps_IsDeferredEvenWithinTheTrack()
    {
        (SyncScenarioHarness h, _, _, _) = ArrangeContinueWithA();

        Raw(h, 12.0, 10_080, detectedFps: 24.0);

        h.Operations.Should().NotContain(o => o.Name == "seek", "fps 推定の食い違いは未確認扱い");

        Raw(h, 12.04, 10_120, detectedFps: 25.0);
        Raw(h, 12.08, 10_160, detectedFps: 25.0);
        Raw(h, 12.12, 10_200, detectedFps: 25.0);

        h.Operations.Should().Contain(o => o.Name == "seek", "次フレームが連続すれば適用する");
    }

    [Fact]
    public void HeldLossJump_RecoversOnlyAfterConfirmation()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock)
        {
            SignalLossMode = LtcSignalLossMode.Stop,
            GapBehavior = GapBehavior.Black,
        };
        h.AddTrack("A", 5, 20);
        PlaylistTrack b = h.AddTrack("B", 30, 20);
        h.ReloadProject();
        h.ManualPlay();
        Raw(h, 7.0, 10_000);
        Raw(h, 7.04, 10_040);
        Raw(h, 7.04, 10_200);
        Raw(h, 7.04, 10_300);
        for (int i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            h.Tick100Milliseconds();
        }

        h.IsPaused.Should().BeTrue("保持の損失で停止する");
        h.Operations.Clear();

        Raw(h, 40.0, 10_540);

        h.IsPaused.Should().BeTrue("未確認の Jump 1 枚では保持損失から復帰しない");
        h.Operations.Should().NotContain(o => o.Name == "signal-loss-resume" || o.Name == "loadfile");

        Raw(h, 40.04, 10_580);

        h.IsPaused.Should().BeFalse("確認済みの Jump で復帰する");
        h.Operations.Should().Contain(o => o.Name == "signal-loss-resume");
        h.Operations.Should().Contain(o => o.Name == "loadfile");
        h.LoadedTrackId.Should().Be(b.Id);
    }
}
