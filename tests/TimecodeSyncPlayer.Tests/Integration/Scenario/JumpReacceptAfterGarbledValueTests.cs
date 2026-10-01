using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.6.1 段 B（docs/design/v0.6.1-jump-confirm.md 3-5 節の「帯で受理する」、5 節の T1・T1b・T1c・T1d・T2）: 化けた 1 枚（G）の
/// 次に届いた正しい値（B）が、G との差で Jump の保留になり、次の 1 枚で「確定した Jump」として受理し直されていた
/// （受理済みの値 A から見れば B は連続）。その下流で後ろ向きの同期シークが出た。
/// 台本は設計書 11 節の実機の列（検証機のアプリのログ）の時刻と値をそのまま使う。
/// - 同じ ms に並ぶ行は同じ WASAPI のコールバックで届いたもの（間隔 0ms）として送る
/// - CONFIRM の行は、直前の行が同じ値の Duplicate ならそのフレーム自身の確認のログ（別のフレームではない）。
///   それ以外は、状態が Normal の確認のフレーム（WRN の行に出ない）として送る
/// - ログの Reverse・Duplicate の状態を診断で再現するのに要る、WRN の行に出ない Normal のフレーム（隠れた Normal）を、
///   それを要する行と同じ ms の直前に足す（台本の <c>Hidden</c>）
/// - 化けた値のうち 30fps のフレーム番号に乗らない値（61.995）は、最も近いフレーム（62.000）で送る（どちらも Jump）
/// - 台本の後は、送出が戻った流れ（最後の値から 30fps の Normal）を 1 秒続ける（実機の送出は続いている）
/// 期待（承認済み）: 後ろ向きのシークは 1 本まで、受け直しの確定した Jump は 0、化けた値を 1 枚で受理しない、
/// 送出が戻った値は (ii) の流れか確定した Jump で受理される、復帰の後の差は許容（0.24 秒）内。
/// </summary>
[Collection("Serilog global logger")]
public class JumpReacceptAfterGarbledValueTests
{
    private readonly ITestOutputHelper _output;

    public JumpReacceptAfterGarbledValueTests(ITestOutputHelper output) => _output = output;

    private const int FrameMs = 33;
    private const double ToleranceSeconds = 0.24;

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

        public List<LogEvent> Snapshot()
        {
            lock (Sink.Events) return Sink.Events.ToList();
        }

        public void Dispose() => Log.Logger = _previous;
    }

    /// <summary>台本の 1 行（経過 ms、値、検出 fps、隠れた Normal か）。</summary>
    private readonly record struct Row(int Ms, double Seconds, double DetectedFps = 30.0, bool Hidden = false);

    private static Row Hidden(int ms, double seconds) => new(ms, seconds, 30.0, Hidden: true);

    /// <summary>r1（v0.5.4 候補 1、Fixed30、17:11:09.8〜10.9）。受理 72.200 の後。</summary>
    private static readonly Row[] R1 =
    [
        new(0, 0.000), new(0, 72.167), new(0, 72.200),          // CONFIRM 72.200（Normal）
        new(58, 72.167), new(59, 72.167),
        Hidden(172, 72.200), new(172, 72.167),                  // Rev 72.167 には間に進んだ値が要る
        new(226, 72.067), new(226, 72.200),
        new(336, 67.000), new(336, 72.200), new(336, 72.233),   // CONFIRM 72.233（Normal）
        new(448, 56414.959), new(448, 72.200), new(448, 72.233), // CONFIRM 72.233（Normal）。451 SEEK −0.477
        new(505, 72.200),
        new(558, 70.667), new(561, 72.233),
        new(672, 60.700), new(672, 72.233),
        new(725, 60.667), new(725, 72.233),
        new(787, 72.233),                                        // Dup（788 の CONFIRM はこのフレームの確認）
        new(875, 72.233, 24.0),
        new(892, 33344.000, 24.0), new(892, 72.233, 24.0),
        new(993, 72.733),
        new(1044, 72.767),                                       // CONFIRM 72.767（本物の Jump の確認）
    ];

    /// <summary>A-1（v0.6.0 候補 1 の A 切替 1 本目、Fixed30、21:24:08.7〜10.0）。受理 118.400 の後。</summary>
    private static readonly Row[] A1 =
    [
        new(0, 118.800), new(0, 118.400),
        new(117, 118.000), new(117, 118.400),
        new(175, 62.000), new(176, 118.400), new(176, 118.433),  // 61.995 → 62.000。CONFIRM 118.433（Normal）
        new(238, 118.400),                                        // 302 SEEK −0.210
        new(356, 118.033), new(356, 118.433),
        new(475, 60.000), new(475, 118.433), new(476, 118.467),  // CONFIRM 118.467（Normal）
        new(586, 118.433), Hidden(587, 118.467), new(587, 118.467),
        new(699, 118.433),                                        // 759 SEEK −0.316
        Hidden(820, 118.467), new(820, 118.467),
        new(971, 118.467, 24.0),
        new(1071, 118.967),
        new(1122, 119.000),                                       // CONFIRM 119.000（本物の Jump の確認）
    ];

    /// <summary>A-2（v0.6.0 候補 2 の A 切替 2 本目、Fixed30、05:30:38.7〜39.8）。受理 163.600 の後。</summary>
    private static readonly Row[] A2 =
    [
        new(0, 160.000), new(0, 163.633),
        new(60, 160.000), new(60, 163.633),
        new(113, 163.633),                                        // Dup（CONFIRM はこのフレームの確認）
        Hidden(171, 163.667), new(171, 163.633),
        Hidden(226, 163.667), new(226, 163.633),                  // 228 SEEK −0.291
        new(338, 163.000), new(338, 163.667),
        new(396, 0.000), new(396, 163.667),
        new(452, 163.667),                                        // Dup（CONFIRM はこのフレームの確認）
        Hidden(513, 163.700), new(513, 163.667), new(513, 0.367), new(513, 163.700),
        new(632, 163.667),
        Hidden(689, 163.700), new(689, 163.700), new(690, 163.700),
        new(809, 163.700),
        new(897, 163.700, 24.0),
        new(1014, 164.233),
        new(1067, 164.267),                                       // CONFIRM 164.267（本物の Jump の確認）
    ];

    private static readonly double[] R1Garbled = [0.000, 72.067, 67.000, 56414.959, 70.667, 60.700, 60.667, 33344.000];
    private static readonly double[] A1Garbled = [118.800, 118.000, 62.000, 118.033, 60.000];
    private static readonly double[] A2Garbled = [160.000, 163.000, 0.000, 0.367];

    private static ScenarioClock NewClock() =>
        new(new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero), monotonicMilliseconds: 50_000);

    private static SyncScenarioHarness Arrange(
        ScenarioClock clock, TimecodeFpsMode fpsMode, double videoFps, LtcSignalLossMode lossMode)
    {
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = lossMode,
            FpsMode = fpsMode,
        };
        h.AddTrack("A", 0, 300);
        h.ReloadProject();
        h.SetDurationSeconds(300);
        h.Playback.SetFps(videoFps);
        h.ManualPlay();
        return h;
    }

    /// <summary>30fps の番号で timecode を作り、検出 fps とフレーム終端（サンプル時計）を付けて実の受信経路へ渡す。</summary>
    private static void Frame(SyncScenarioHarness h, ScenarioClock clock, double seconds, double detectedFps = 30.0)
    {
        int frame = (int)Math.Round(seconds * 30.0);
        var timecode = new LtcTimecode(
            frame / (30 * 3600), frame / (30 * 60) % 60, frame / 30 % 60, frame % 30, false);
        h.Controller.ReceiveFrame(
            new LtcFrameReceivedEventArgs(timecode, detectedFps, seconds, clock.Qpc, clock.Qpc),
            clock.MonotonicMilliseconds);
    }

    private static void AdvanceTo(SyncScenarioHarness h, ScenarioClock clock, long targetMilliseconds)
    {
        while (clock.MonotonicMilliseconds < targetMilliseconds)
            h.AdvanceMilliseconds((int)Math.Min(FrameMs, targetMilliseconds - clock.MonotonicMilliseconds));
    }

    /// <summary>受理値 <paramref name="accepted"/> で終わる 1 秒の Normal を送り、映像も同じ位置で走らせる。</summary>
    private static void Preamble(SyncScenarioHarness h, ScenarioClock clock, double accepted)
    {
        double start = accepted - 1.0;
        h.AdvancePlayback(start);
        for (int i = 0; i <= 30; i++)
        {
            Frame(h, clock, start + i / 30.0);
            h.AdvanceMilliseconds(FrameMs);
        }
    }

    /// <summary>台本を送り、送出が戻った流れ（最後の値から 30fps の Normal）を 1 秒続ける。</summary>
    private static void Play(
        SyncScenarioHarness h, ScenarioClock clock, Row[] rows, List<double> pausedDuring,
        LoggerCapture capture, long origin, List<string> holdExits)
    {
        foreach (Row row in rows)
        {
            AdvanceTo(h, clock, origin + row.Ms);
            ObservedFrame(h, clock, row.Seconds, row.DetectedFps, capture, origin, holdExits);
            if (h.IsPaused)
                pausedDuring.Add(row.Seconds);
        }
        double last = rows[^1].Seconds;
        for (int i = 1; i <= 30; i++)
        {
            AdvanceTo(h, clock, clock.MonotonicMilliseconds + FrameMs);
            ObservedFrame(h, clock, last + i / 30.0, 30.0, capture, origin, holdExits);
        }
    }

    /// <summary>
    /// 1 枚を送り、そのフレームで保持（規則 4: マスターが止まっている）に入った・抜けた、またはシークが出たなら、
    /// 時刻・値・層 2 の分類を記録する（再現の調べ用）。
    /// </summary>
    private static void ObservedFrame(
        SyncScenarioHarness h, ScenarioClock clock, double seconds, double detectedFps,
        LoggerCapture capture, long origin, List<string> holdExits)
    {
        bool heldBefore = MasterStopped(h);
        int logBefore = capture.Snapshot().Count;
        int eventsBefore = h.Events.Count;
        Frame(h, clock, seconds, detectedFps);
        bool heldAfter = MasterStopped(h);
        bool seeked = h.Events.Skip(eventsBefore).Any(e => e.Kind == "seek");
        string? change = (heldBefore, heldAfter) switch
        {
            (true, false) => "exit",
            (false, true) => "enter",
            _ => null,
        };
        if (change is null && !seeked)
            return;
        string layer2 = capture.Snapshot().Skip(logBefore)
            .Where(e => e.MessageTemplate.Text.StartsWith("LTC frame layer2 class", StringComparison.Ordinal))
            .Select(e => e.Properties.TryGetValue("Layer2", out LogEventPropertyValue? v) ? v.ToString() : "?")
            .LastOrDefault() ?? "-";
        string what = string.Join("+", new[] { change, seeked ? "seek" : null }.Where(x => x is not null));
        holdExits.Add($"{clock.MonotonicMilliseconds - origin}ms {seconds:F3} {what} class={layer2} stopped={heldAfter}");
    }

    private static double Ltc(LogEvent e) =>
        e.Properties.TryGetValue("Ltc", out LogEventPropertyValue? value) && value is ScalarValue { Value: double v }
            ? v : double.NaN;

    private static bool MasterStopped(SyncScenarioHarness h) => h.SyncService.MasterStoppedSource?.Invoke() ?? false;

    /// <summary>シークの目標と、その直前の歩みで観測した再生位置との差（負 = 後ろ向き）と、台本の 0 からの時刻。</summary>
    private static List<(double Target, double Delta, long AtMs)> SeeksWithDelta(SyncScenarioHarness h, int fromEvent, long origin)
    {
        var result = new List<(double, double, long)>();
        double position = double.NaN;
        foreach (ScenarioEvent e in h.Events.Skip(fromEvent))
        {
            if (e.Kind == "tick" && e.Value is double p)
                position = p;
            else if (e.Kind == "seek" && e.Value is double target)
                result.Add((target, target - position, e.AtMilliseconds - origin));
        }
        return result;
    }

    private sealed record Outcome(
        List<double> Confirmed, List<double> Applied, List<(double Target, double Delta, long AtMs)> Seeks,
        List<double> PausedDuring, double FinalDifference, bool FinallyPaused, List<string> RelocateReasons,
        List<string> HoldExits)
    {
        public string Summary =>
            $"confirmed=[{string.Join(", ", Confirmed.Select(v => v.ToString("F3")))}] " +
            $"relocate=[{string.Join(", ", RelocateReasons)}] " +
            $"seeks=[{string.Join(", ", Seeks.Select(s => $"{s.AtMs}ms {s.Target:F3}({s.Delta:+0.000;-0.000})"))}] " +
            $"holdTrace=[{string.Join(", ", HoldExits)}] " +
            $"finalDifference={FinalDifference:+0.000;-0.000} pausedDuring={PausedDuring.Count} finallyPaused={FinallyPaused}";
    }

    private static Outcome Run(
        Row[] rows, double accepted, TimecodeFpsMode fpsMode, double videoFps,
        LtcSignalLossMode lossMode = LtcSignalLossMode.RunThrough)
    {
        using var capture = new LoggerCapture();
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, fpsMode, videoFps, lossMode);
        Preamble(h, clock, accepted);
        int fromEvent = h.Events.Count;
        int fromLog = capture.Snapshot().Count;
        var pausedDuring = new List<double>();
        var holdExits = new List<string>();
        long origin = clock.MonotonicMilliseconds;

        Play(h, clock, rows, pausedDuring, capture, origin, holdExits);

        List<LogEvent> after = capture.Snapshot().Skip(fromLog).ToList();
        List<double> confirmed = after
            .Where(e => e.MessageTemplate.Text.Contains("applying the confirmed Jump frame")).Select(Ltc).ToList();
        List<double> applied = after
            .Where(e => e.MessageTemplate.Text.StartsWith("sync.apply", StringComparison.Ordinal)).Select(Ltc).ToList();
        List<string> reasons = after
            .Where(e => e.MessageTemplate.Text.StartsWith("sync.gate relocate", StringComparison.Ordinal))
            .Select(e => e.Properties.TryGetValue("Reason", out LogEventPropertyValue? r) ? r.ToString().Trim('"') : "?")
            .ToList();
        return new Outcome(confirmed, applied, SeeksWithDelta(h, fromEvent, origin), pausedDuring,
            h.PlaybackSeconds - h.Controller.LastLtcSeconds, h.IsPaused, reasons, holdExits);
    }

    /// <summary>結果（確定・relocate の理由・シークの時刻と差・保持に入った／抜けた枠とシークを出した枠の時刻と分類）をテストの出力に書く。</summary>
    private Outcome Reported(Outcome outcome)
    {
        _output.WriteLine(outcome.Summary);
        return outcome;
    }

    private static void AssertApproved(Outcome outcome, double firstRealResume, double[] garbled)
    {
        outcome.Seeks.Count(s => s.Delta < -1.0 / 30.0).Should().BeLessThanOrEqualTo(1,
            "後ろ向きのシークは規則 3・4 による 1 本まで。" + outcome.Summary);
        outcome.Confirmed.Where(v => v < firstRealResume - 1e-3).Should().BeEmpty(
            "受け直しの確定した Jump は 0（確定しうるのは送出が戻った本物の Jump だけ）。" + outcome.Summary);
        outcome.Applied.Where(v => garbled.Any(g => Math.Abs(v - g) < 0.1)).Should().BeEmpty(
            "化けた値を 1 枚で受理しない。" + outcome.Summary);
        outcome.Applied.Should().Contain(v => v >= firstRealResume - 1e-3,
            "送出が戻った値は (ii) の流れか確定した Jump で受理される。" + outcome.Summary);
        Math.Abs(outcome.FinalDifference).Should().BeLessThanOrEqualTo(ToleranceSeconds,
            "復帰の後の差は許容内。" + outcome.Summary);
    }

    /// <summary>T1: r1 の列（Fixed30、RunThrough）。今は正しい値の受け直しで確定した Jump が 4 回出る。</summary>
    [Fact]
    public void T1_R1_Fixed30_GarbledValues_DoNotReacceptTheAcceptedStream() =>
        AssertApproved(Reported(Run(R1, 72.200, TimecodeFpsMode.Fixed30, videoFps: 30)), 72.733, R1Garbled);

    /// <summary>T1b: A-1 の列（Fixed30、M5 = ProRes 4K60 の再生中）。今は受け直し 2 回と後ろ向きのシーク 2 本。</summary>
    [Fact]
    public void T1b_A1_Fixed30_GarbledValues_DoNotReacceptTheAcceptedStream() =>
        AssertApproved(Reported(Run(A1, 118.400, TimecodeFpsMode.Fixed30, videoFps: 60)), 118.967, A1Garbled);

    /// <summary>
    /// T1c: A-2 の列（Fixed30、M7 = H.264 1080p60）。確定は fps の疑わしくない同値の Duplicate なので D2 では止まらず、
    /// 層 2 の分類（戻った正しい値を A の流れとして捨て、保留を残さない）だけで止まる。
    /// </summary>
    [Fact]
    public void T1c_A2_Fixed30_GarbledValues_DoNotReacceptTheAcceptedStream() =>
        AssertApproved(Reported(Run(A2, 163.600, TimecodeFpsMode.Fixed30, videoFps: 60)), 164.233, A2Garbled);

    /// <summary>T2: T1 の型を Auto で（層 2 の分類は fps モードに依らない）。</summary>
    [Fact]
    public void T2_R1_Auto_GarbledValues_DoNotReacceptTheAcceptedStream() =>
        AssertApproved(Reported(Run(R1, 72.200, TimecodeFpsMode.Auto, videoFps: 30)), 72.733, R1Garbled);

    /// <summary>
    /// T1d: r1 の列を停止モード（Stop）で。送出が止まっている間は保持の損失で映像が止まり、送出が戻ったら復帰して
    /// 前向きの relocate が 1 本出る（RunThrough の T1 と対）。
    /// </summary>
    [Fact]
    public void T1d_R1_StopMode_HoldsWhileStalled_ThenOneForwardRelocateOnResume()
    {
        Outcome outcome = Reported(Run(R1, 72.200, TimecodeFpsMode.Fixed30, videoFps: 30, LtcSignalLossMode.Stop));

        AssertApproved(outcome, 72.733, R1Garbled);
        outcome.PausedDuring.Should().NotBeEmpty("送出が止まっている間は保持の損失で映像が止まる。" + outcome.Summary);
        outcome.FinallyPaused.Should().BeFalse("送出が戻ったら復帰する。" + outcome.Summary);
        outcome.Seeks.Count(s => s.Delta > 1.0 / 30.0).Should().Be(1,
            "復帰で前向きの relocate が 1 本。" + outcome.Summary);
    }

    /// <summary>
    /// 設計書 2 節の型: 受理 A の後に化けた G、B（A の Duplicate か A+1）、C（B の Duplicate か B+1）。
    /// 今は B が G との差で Jump の保留になり、C で確定した Jump として受け直す。
    /// </summary>
    [Theory]
    [InlineData(0, TimecodeFpsMode.Fixed30)]
    [InlineData(1, TimecodeFpsMode.Fixed30)]
    [InlineData(0, TimecodeFpsMode.Auto)]
    [InlineData(1, TimecodeFpsMode.Auto)]
    public void GarbledThenBackToTheAcceptedStream_IsNotAConfirmedJump(int bFramesAfterA, TimecodeFpsMode fpsMode)
    {
        const double a = 72.200;
        double b = a + bFramesAfterA / 30.0;
        double c = b + bFramesAfterA / 30.0;
        Row[] rows = [new(0, 33344.000), new(FrameMs, b), new(2 * FrameMs, c)];

        Outcome outcome = Reported(Run(rows, a, fpsMode, videoFps: 30));

        outcome.Confirmed.Should().BeEmpty("B は A から見れば連続（確定した Jump ではない）。" + outcome.Summary);
        outcome.Seeks.Should().BeEmpty("A の流れに戻っただけなのでシークしない。" + outcome.Summary);
    }
}
