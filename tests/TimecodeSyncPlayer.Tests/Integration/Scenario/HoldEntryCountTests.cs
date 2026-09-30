using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4（規則 4 の入口の数え方、検証機の L-2 の回帰）: LTC の音が乱れた最中に、確認した Jump の確認フレーム
/// （Duplicate）と、化けた値（Jump の保留）を挟んだ後の Duplicate の 2 枚を「保持の 2 枚」と数え、RunThrough の
/// 入口の合わせが古い保持値へ後ろ向きのシークを出していた。入口の 2 枚は「連続した 2 枚が同値」で、fps が
/// 疑わしい（Fixed で検出と解決が食い違う）Duplicate は数えない。停止モードの U8 も同じ数え方。
/// </summary>
[Collection("Serilog global logger")]
public class HoldEntryCountTests
{
    private const int FrameMs = 33;

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    private sealed class LoggerCapture : IDisposable
    {
        private readonly ILogger _previous = Log.Logger;
        public ListSink Sink { get; } = new();

        public LoggerCapture() =>
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(Sink).CreateLogger();

        public int Count(string text)
        {
            lock (Sink.Events) return Sink.Events.Count(e => e.MessageTemplate.Text.Contains(text));
        }

        public void Dispose() => Log.Logger = _previous;
    }

    private static SyncScenarioHarness Arrange(
        ScenarioClock clock, TimecodeFpsMode fpsMode, LtcSignalLossMode lossMode)
    {
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = lossMode,
            FpsMode = fpsMode,
        };
        h.AddTrack("A", 0, 20);
        h.AddTrack("B", 30, 20);
        h.ReloadProject();
        h.SetDurationSeconds(20);
        h.ManualPlay();
        h.AdvancePlayback(7.0);
        return h;
    }

    private static ScenarioClock NewClock() =>
        new(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero), monotonicMilliseconds: 50_000);

    /// <summary>30fps の番号で timecode を作り、検出 fps を付けて実の受信経路（fps の解決・診断）へ渡す。</summary>
    private static void Frame(SyncScenarioHarness h, ScenarioClock clock, double seconds, double detectedFps = 30.0)
    {
        int frame = (int)Math.Round(seconds * 30.0);
        var timecode = new LtcTimecode(
            frame / (30 * 3600), frame / (30 * 60) % 60, frame / 30 % 60, frame % 30, false);
        h.Controller.ReceiveFrame(
            new LtcFrameReceivedEventArgs(timecode, detectedFps, seconds, 0, 0), clock.MonotonicMilliseconds);
    }

    /// <summary>7.000〜8.000 を 30fps で進めて追従する（最後の値は 8.000）。</summary>
    private static void Follow(SyncScenarioHarness h, ScenarioClock clock)
    {
        for (int i = 0; i <= 30; i++)
        {
            Frame(h, clock, 7.0 + i / 30.0);
            h.AdvanceMilliseconds(FrameMs);
        }
    }

    /// <summary>
    /// 今回の列: 確認した Jump（確認フレームが Duplicate）→ 化けた値 2 枚（Jump の保留）→ 音が抜けて
    /// 確認の窓を過ぎる → 古い値と同じ Duplicate（fps が食い違うか否かは引数）。
    /// </summary>
    private static void GarbledSequence(SyncScenarioHarness h, ScenarioClock clock, double lastDetectedFps)
    {
        Frame(h, clock, 8.2);                 // Jump（保留）
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 8.2);                 // 確認フレーム（Duplicate）→ 確認した Jump を適用
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 15.0);                // 化けた値（Jump の保留）
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 8.2);                 // 化けた値から戻る（Jump の保留）
        h.AdvanceMilliseconds(400);           // 音が抜けて確認の窓を過ぎる。映像は走り続ける
        h.Operations.Clear();
        Frame(h, clock, 8.2, lastDetectedFps); // 古い値と同じ Duplicate
    }

    [Fact]
    public void Fixed30_GarbledSequence_FpsSuspectDuplicate_DoesNotAlignBackwardOnTheHoldEntry()
    {
        using var capture = new LoggerCapture();
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, TimecodeFpsMode.Fixed30, LtcSignalLossMode.RunThrough);
        Follow(h, clock);

        GarbledSequence(h, clock, lastDetectedFps: 24.0);

        capture.Count("entry alignment seek issued").Should().Be(0,
            "確認フレームと、化けた値を挟んだ fps の疑わしい Duplicate は、連続した保持の 2 枚ではない");
        h.Operations.Should().NotContain(o => o.Name == "seek", "古い保持値へ後ろ向きに合わせない");
    }

    [Fact]
    public void Auto_GarbledSequence_DuplicateAfterPendingJumps_DoesNotAlignBackwardOnTheHoldEntry()
    {
        using var capture = new LoggerCapture();
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, TimecodeFpsMode.Auto, LtcSignalLossMode.RunThrough);
        Follow(h, clock);

        GarbledSequence(h, clock, lastDetectedFps: 30.0);

        capture.Count("entry alignment seek issued").Should().Be(0,
            "間に Jump の保留が挟まった 2 枚は、連続した保持の 2 枚ではない（fps の判定が効かない Auto でも弾く）");
        h.Operations.Should().NotContain(o => o.Name == "seek", "古い保持値へ後ろ向きに合わせない");
    }

    [Fact]
    public void Fixed30_ConsecutiveHold_SecondIsFpsSuspect_IsNotCounted_ThenARealPairAligns()
    {
        using var capture = new LoggerCapture();
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, TimecodeFpsMode.Fixed30, LtcSignalLossMode.RunThrough);
        Follow(h, clock);
        h.AdvanceMilliseconds(400);
        h.Operations.Clear();

        Frame(h, clock, 8.0);
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 8.0, detectedFps: 24.0);
        capture.Count("entry alignment seek issued").Should().Be(0, "fps の疑わしい Duplicate は入口の 2 枚に数えない");

        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 8.0);
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 8.0);
        capture.Count("entry alignment seek issued").Should().Be(1, "疑わしくない連続した同値の 2 枚で入口の合わせが出る");
        h.Operations.Should().Contain(o => o.Name == "seek" && Math.Abs((o.Value ?? double.NaN) - 8.0) < 1e-6);
    }

    [Fact]
    public void RunThrough_RealHold_ConsecutiveSameValueDuplicates_StillAlignOnTheHoldEntry()
    {
        using var capture = new LoggerCapture();
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, TimecodeFpsMode.Fixed30, LtcSignalLossMode.RunThrough);
        Follow(h, clock);
        // マスターが 8.000 で止まった後、映像は 1.0 で走り続けて許容を超えて先へ出る。
        h.AdvanceMilliseconds(400);
        h.Operations.Clear();

        Frame(h, clock, 8.0);
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 8.0);

        capture.Count("entry alignment seek issued").Should().Be(1, "本物の保持（連続した同値の 2 枚）では今どおり合わせる");
        h.Operations.Should().ContainSingle(o => o.Name == "seek")
            .Which.Value.Should().BeApproximately(8.0, 1e-6);
    }

    [Fact]
    public void StopMode_GarbledSequence_DoesNotConfirmTheLossEarlyByU8()
    {
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, TimecodeFpsMode.Fixed30, LtcSignalLossMode.Stop);
        Follow(h, clock);

        // 最後の値の進むフレーム（8.000）から 250ms の確認より前に、今回の列の 2 枚目の Duplicate を届ける。
        Frame(h, clock, 8.2);                 // Jump（保留）
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 8.2);                 // 確認フレーム（Duplicate）
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 15.0);                // 化けた値（Jump の保留）
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 8.2);                 // 化けた値から戻る（Jump の保留）
        h.AdvanceMilliseconds(110);           // 確認の窓（100ms）を過ぎる
        h.Operations.Clear();
        Frame(h, clock, 8.2);                 // 古い値と同じ Duplicate
        h.AdvanceMilliseconds(5);             // Tick（最後の進むフレームから約 250ms 未満）

        h.Operations.Should().NotContain(o => o.Name == "signal-loss-pause",
            "連続していない 2 枚で U8 の即時の損失確定をしない（250ms の確認は今どおり）");
        h.IsPaused.Should().BeFalse();
    }
}
