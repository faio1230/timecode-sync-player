using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.6.1 3-5 節の (iii) 這う前進（レビューの直し、TSP-Fable の判断）: 受理済みの値から +1〜数フレームだけ、実時間より遅れて
/// 進んだ値。A と時刻を付け直すが、M は止める（外挿しない）。合わせはモードで分ける。
/// - 停止モードで信号断が止めている間: 止めたまま新しい保持値へ 1 回着地する（1 フレーム精度）。再生は走らない
/// - ランスルー（と止めていない間）: 合わせない
/// - 保持の外の 1 枚（走行中の遅れた 1 枚）は保持着地の記録を立てない（規則 4 の入口の合わせを消さない）
/// </summary>
[Collection("Serilog global logger")]
public class CreepingAdvanceTests
{
    private readonly ITestOutputHelper _output;

    public CreepingAdvanceTests(ITestOutputHelper output) => _output = output;

    private const int FrameMs = 33;

    private static SyncScenarioHarness Arrange(ScenarioClock clock, LtcSignalLossMode lossMode)
    {
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = lossMode,
            FpsMode = TimecodeFpsMode.Fixed30,
        };
        h.AddTrack("A", 0, 60);
        h.ReloadProject();
        h.SetDurationSeconds(60);
        h.Playback.SetFps(60);   // 着地の 1 フレーム精度（映像の 1 フレーム）が LTC の 1 歩より細かい素材
        h.ManualPlay();
        h.AdvancePlayback(9.0);
        return h;
    }

    private static ScenarioClock NewClock() =>
        new(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), monotonicMilliseconds: 50_000);

    /// <summary>30fps の番号で timecode を作り、実の受信経路（fps の解決・診断）へ渡す。</summary>
    private static void Frame(SyncScenarioHarness h, ScenarioClock clock, double seconds)
    {
        int frame = (int)Math.Round(seconds * 30.0);
        var timecode = new LtcTimecode(
            frame / (30 * 3600), frame / (30 * 60) % 60, frame / 30 % 60, frame % 30, false);
        h.Controller.ReceiveFrame(
            new LtcFrameReceivedEventArgs(timecode, 30.0, seconds, clock.Qpc, clock.Qpc), clock.MonotonicMilliseconds);
    }

    /// <summary>9.000〜10.000 を 30fps で追従する。</summary>
    private static void Follow(SyncScenarioHarness h, ScenarioClock clock)
    {
        for (int i = 0; i <= 30; i++)
        {
            Frame(h, clock, 9.0 + i / 30.0);
            h.AdvanceMilliseconds(FrameMs);
        }
    }

    private static List<double> Seeks(SyncScenarioHarness h) =>
        h.Operations.Where(o => o.Name == "seek").Select(o => o.Value ?? double.NaN).ToList();

    [Fact]
    public void StopMode_PausedHold_CreepingAdvance_LandsOnceOnTheNewValueWithoutResuming()
    {
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, LtcSignalLossMode.Stop);
        Follow(h, clock);
        for (int i = 0; i < 8; i++)
        {
            Frame(h, clock, 10.0);   // 保持（U8 で一時停止）
            h.AdvanceMilliseconds(FrameMs);
        }
        h.IsPaused.Should().BeTrue("前提: 保持で一時停止");
        h.Operations.Clear();

        Frame(h, clock, 10.0 + 1.0 / 30.0);   // 這う前進（+1 フレーム、実時間より遅い）
        h.AdvanceMilliseconds(FrameMs);
        for (int i = 0; i < 4; i++)
        {
            Frame(h, clock, 10.0 + 1.0 / 30.0);   // その値の保持
            h.AdvanceMilliseconds(FrameMs);
        }
        Frame(h, clock, 10.0 + 2.0 / 30.0);   // もう 1 歩
        h.AdvanceMilliseconds(FrameMs);

        h.IsPaused.Should().BeTrue("這う前進では再生を走らせない");
        Seeks(h).Should().HaveCount(2, "1 歩ごとに止めたまま 1 回着地する（その後の保持では着地を繰り返さない）");
        Seeks(h)[0].Should().BeApproximately(10.0 + 1.0 / 30.0, 1e-6);
        Seeks(h)[1].Should().BeApproximately(10.0 + 2.0 / 30.0, 1e-6);
    }

    [Fact]
    public void RunThrough_HeldThenCreepingAdvance_DoesNotAlign()
    {
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, LtcSignalLossMode.RunThrough);
        Follow(h, clock);
        for (int i = 0; i < 3; i++)
        {
            Frame(h, clock, 10.0);   // 保持（規則 4 の入口の 1 回の合わせ）
            h.AdvanceMilliseconds(FrameMs);
        }
        for (int i = 0; i < 6; i++)
            h.AdvanceMilliseconds(FrameMs);   // 送出が止まっている間、映像は 1.0 で走る
        h.Operations.Clear();

        Frame(h, clock, 10.0 + 1.0 / 30.0);   // 這う前進
        h.AdvanceMilliseconds(FrameMs);

        Seeks(h).Should().BeEmpty("ランスルーの這う前進では合わせない（M を止めるだけ）");
    }

    [Fact]
    public void RunThrough_LateFrameWhileRunning_DoesNotSuppressTheNextHoldEntry()
    {
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, LtcSignalLossMode.RunThrough);
        Follow(h, clock);
        // 走行中の遅れた 1 枚: 200ms 届かず、+1 フレームだけ進んだ値（這う前進）。保持の外。
        for (int i = 0; i < 6; i++)
            h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 10.0 + 1.0 / 30.0);
        h.AdvanceMilliseconds(FrameMs);
        // 送出はその値で止まる（同値の保持）。映像は先へ走っているので、規則 4 の入口で 1 回合わせる。
        for (int i = 0; i < 6; i++)
            h.AdvanceMilliseconds(FrameMs);
        h.Operations.Clear();
        Frame(h, clock, 10.0 + 1.0 / 30.0);
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 10.0 + 1.0 / 30.0);
        h.AdvanceMilliseconds(FrameMs);

        Seeks(h).Should().ContainSingle("保持の外の這う前進は保持着地の記録を立てないので、入口の合わせが出る")
            .Which.Should().BeApproximately(10.0 + 1.0 / 30.0, 1e-6);
    }

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    /// <summary>
    /// レビューの 7: 這う前進と保留の捨ての件数を、監視の停止のときに Information で 1 行出す（配布ビルドでも数えられる）。
    /// </summary>
    [Fact]
    public void Layer2Summary_IsLoggedOnceAtInformationWhenMonitoringStops()
    {
        ILogger previous = Log.Logger;
        var sink = new ListSink();
        Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink).CreateLogger();
        try
        {
            ScenarioClock clock = NewClock();
            SyncScenarioHarness h = Arrange(clock, LtcSignalLossMode.RunThrough);
            Follow(h, clock);
            for (int i = 0; i < 6; i++)
                h.AdvanceMilliseconds(FrameMs);
            Frame(h, clock, 10.0 + 1.0 / 30.0);   // 這う前進 1 件
            h.AdvanceMilliseconds(FrameMs);
            Frame(h, clock, 55.5);                // 化けた値（保留）
            h.AdvanceMilliseconds(FrameMs);
            Frame(h, clock, 10.0 + 2.0 / 30.0);   // A の流れへ戻る（保留の捨て 1 件）

            h.IsMonitoring = false;

            List<LogEvent> summaries;
            lock (sink.Events)
                summaries = sink.Events.Where(e => e.MessageTemplate.Text.StartsWith("LTC layer2 summary", StringComparison.Ordinal)).ToList();
            summaries.Should().ContainSingle();
            summaries[0].Level.Should().Be(LogEventLevel.Information);
            summaries[0].Properties["CreepingAdvances"].ToString().Should().Be("1");
            summaries[0].Properties["ReturnedToAcceptedStream"].ToString().Should().Be("1");
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    /// <summary>
    /// レビューの 6: 0.25 倍速の送出（133ms ごとに 1 歩）を停止モードで。毎フレームが這う前進になる間も、保持の枝の処理を
    /// 通し、損失からの復帰と U8 の再停止の往復（signal-loss-resume と signal-loss-pause の繰り返し）が出ない。
    /// </summary>
    [Fact]
    public void StopMode_QuarterSpeedSender_DoesNotOscillateBetweenResumeAndPause()
    {
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, LtcSignalLossMode.Stop);
        Follow(h, clock);
        h.Operations.Clear();

        long origin = clock.MonotonicMilliseconds;
        var oscillation = new List<string>();
        double value = 10.0;
        for (int step = 0; step < 30; step++)   // 約 4 秒
        {
            value += 1.0 / 30.0;
            int before = h.Operations.Count;
            Frame(h, clock, value);
            NoteLossOperations(h, before, $"{clock.MonotonicMilliseconds - origin}ms frame {value:F3}", oscillation);
            for (int i = 0; i < 4; i++)
            {
                before = h.Operations.Count;
                h.AdvanceMilliseconds(FrameMs + (i == 0 ? 1 : 0));   // 133ms
                NoteLossOperations(h, before, $"{clock.MonotonicMilliseconds - origin}ms tick", oscillation);
            }
        }

        int resumes = h.Operations.Count(o => o.Name == "signal-loss-resume");
        int pauses = h.Operations.Count(o => o.Name == "signal-loss-pause");
        _output.WriteLine($"resume={resumes} pause={pauses}");
        foreach (string line in oscillation)
            _output.WriteLine(line);
        (resumes + pauses).Should().BeLessThanOrEqualTo(1,
            $"遅い送出の間に復帰と再停止を往復しない（resume={resumes} pause={pauses}）");
    }

    /// <summary>
    /// v0.6.1 β (A)（TSP-Fable の判断 (b)）: 無音の損失の後、止まった位置から等速で再開すると、1 枚目は這う前進（e が大きい）で
    /// 復帰の有効フレームに数えない。3 枚では復帰せず、4 枚目（等速の 3 枚目）で復帰する。
    /// </summary>
    [Fact]
    public void StopMode_ResumeFromSilence_FirstFrameIsCreepAndRecoversOnTheFourthFrame()
    {
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, LtcSignalLossMode.Stop);
        Follow(h, clock);
        for (int i = 0; i < 12; i++)
            h.AdvanceMilliseconds(FrameMs);   // 約 400ms の無音（信号断で一時停止）
        h.IsPaused.Should().BeTrue("前提: 無音の損失で一時停止");
        h.Operations.Clear();

        double value = 10.0;
        for (int step = 1; step <= 3; step++)
        {
            value += 1.0 / 30.0;
            Frame(h, clock, value);
            h.AdvanceMilliseconds(FrameMs);
        }
        h.Operations.Should().NotContain(o => o.Name == "signal-loss-resume",
            "1 枚目は這う前進で数えず、等速の 2 枚では復帰しない");
        h.IsPaused.Should().BeTrue();

        value += 1.0 / 30.0;
        Frame(h, clock, value);

        h.Operations.Count(o => o.Name == "signal-loss-resume").Should().Be(1, "4 枚目（等速の 3 枚目）で復帰する");
        h.IsPaused.Should().BeFalse();
    }

    /// <summary>直前の数から増えた損失の停止・復帰を、時刻と契機（フレームか歩みか）付きで記録する（再現の調べ用）。</summary>
    private static void NoteLossOperations(SyncScenarioHarness h, int before, string when, List<string> lines)
    {
        foreach (ScenarioPlaybackOperation op in h.Operations.Skip(before))
        {
            if (op.Name is "signal-loss-resume" or "signal-loss-pause")
                lines.Add($"{when}: {op.Name}");
        }
    }
}
