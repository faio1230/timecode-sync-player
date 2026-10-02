using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.6.4 2-1（#14）: Sync hold summary の数え方の定義。backwardSeeksWhileStopped は「止まった判定から出る後ろ向き」（停止モードの
/// 止まった値への着地）だけ。(C)（損失中に Duplicate で確定した Jump）は heldJumpLandings（総数と、そのうちマスター停止中の後ろ向き
/// heldJumpBackwardWhileStopped）、それ以外でマスター停止中に出た後ろ向き（確定した Jump の後の切替の合わせなど）は
/// otherBackwardWhileStopped。構成上 backwardSeeksWhileStopped + heldJumpBackwardWhileStopped + otherBackwardWhileStopped が
/// 旧 backwardSeeksWhileStopped（v0.6.3）と一致する。判定のコードは変えない。数は行（Information）から読む。
/// </summary>
[Collection("Serilog global logger")]
public class SyncHoldSummaryDefinitionTests
{
    private const long BaseMilliseconds = 50_000;

    private readonly ITestOutputHelper _output;

    public SyncHoldSummaryDefinitionTests(ITestOutputHelper output) => _output = output;

    private sealed class SummarySink : ILogEventSink, IDisposable
    {
        private readonly ILogger _previous = Log.Logger;
        private readonly int _owner = Environment.CurrentManagedThreadId;
        public List<string> Lines { get; } = new();
        public SummarySink() => Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(this).CreateLogger();
        public void Emit(LogEvent e)
        {
            if (Environment.CurrentManagedThreadId == _owner &&
                e.MessageTemplate.Text.StartsWith("Sync hold summary", StringComparison.Ordinal))
                lock (Lines) Lines.Add(e.RenderMessage());
        }
        public void Dispose() => Log.Logger = _previous;
    }

    /// <summary>行から key=数 を読む（無ければ null）。</summary>
    private static long? Field(string line, string key)
    {
        Match m = Regex.Match(line, @"\b" + key + @"=(\d+)");
        return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static ScenarioClock NewClock() =>
        new(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), monotonicMilliseconds: BaseMilliseconds);

    private static void RunScript(SyncScenarioHarness h, ScenarioClock clock)
    {
        long end = h.Ltc.NextMilliseconds + 400;
        while (clock.MonotonicMilliseconds < end)
            h.AdvanceMilliseconds(40);
    }

    /// <summary>監視を止めて要約の行を 1 本読み、出力に書く。</summary>
    private string Summary(SyncScenarioHarness h, SummarySink sink)
    {
        h.IsMonitoring = false;
        string line;
        lock (sink.Lines) line = sink.Lines.Single();
        _output.WriteLine(line);
        return line;
    }

    /// <summary>
    /// (C): 追従 → 保持（損失）→ 損失中に 8.0 へ飛んで Duplicate で確定。旧の数え方では RunThrough が後ろ向き 1（8.0 への 1 本）、
    /// 停止モードは 2（保持の 13.0 への止まった値の着地 1 本と、8.0 への 1 本）。止まった値の着地だけが backwardSeeksWhileStopped に残る。
    /// </summary>
    [Theory]
    [InlineData(LtcSignalLossMode.RunThrough, 0, 1)]
    [InlineData(LtcSignalLossMode.Stop, 1, 2)]
    public void HeldJumpLanding_IsCountedSeparately(LtcSignalLossMode lossMode, long expectedStopLandings, long legacyBackward)
    {
        using var sink = new SummarySink();
        ScenarioClock clock = NewClock();
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true) { SignalLossMode = lossMode };
        h.AddTrack("A", 0, 120);
        h.ManualPlay();
        h.AdvancePlayback(10.0);
        h.Ltc.Normal(10.0, TimeSpan.FromSeconds(3));
        h.Ltc.Duplicate(13.0, TimeSpan.FromSeconds(1));    // 保持（損失）
        h.Ltc.Duplicate(8.0, TimeSpan.FromSeconds(2));     // 損失中に 8.0 へ飛んで、Duplicate で確定（(C)）
        RunScript(h, clock);
        string line = Summary(h, sink);

        Field(line, "backwardSeeksWhileStopped").Should().Be(expectedStopLandings,
            "(C) は止まった判定から出る後ろ向きではない（停止モードの保持の 13.0 への着地だけが残る）");
        Field(line, "heldJumpLandings").Should().Be(1, "(C) の 1 回の合わせ");
        Field(line, "heldJumpBackwardWhileStopped").Should().Be(1, "8.0 への 1 本は後ろ向き");
        (Field(line, "backwardSeeksWhileStopped") + Field(line, "heldJumpBackwardWhileStopped") + Field(line, "otherBackwardWhileStopped"))
            .Should().Be(legacyBackward, "3 つの和は旧 backwardSeeksWhileStopped と一致する");
        Field(line, "otherBackwardWhileStopped").Should().Be(0);
    }

    /// <summary>
    /// 確定した Jump の後の切替（RunThrough）: A で c を学習 → B へ飛んで +1 フレームで確定（切替の読み込みは c を足した位置）→ 保持。
    /// 回収待ちの解除の再適用が止まった値へ後ろ向きに 1 本合わせる。旧の数え方では後ろ向き 1。
    /// </summary>
    [Fact]
    public void SwitchAfterConfirmedJump_BackwardIsCountedAsOther()
    {
        using var sink = new SummarySink();
        ScenarioClock clock = NewClock();
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true) { SignalLossMode = LtcSignalLossMode.RunThrough };
        h.AddTrack("A", 0, 20);
        h.AddTrack("B", 25, 20);
        h.ManualPlay();
        h.AdvancePlayback(10.0);
        h.Playback.SeekLandingDelaySeconds = 0.33;
        h.Ltc.Normal(10.0, TimeSpan.FromSeconds(2));
        h.Ltc.Normal(14.0, TimeSpan.FromSeconds(3));       // A の中で 2 秒先へ（c を学習）
        h.Ltc.Jump(35.0);                                   // B へ飛ぶ
        h.Ltc.Normal(35.04, TimeSpan.FromMilliseconds(40)); // +1 フレームで確定
        h.Ltc.Duplicate(35.04, TimeSpan.FromSeconds(2));    // 保持
        RunScript(h, clock);
        string line = Summary(h, sink);

        Field(line, "backwardSeeksWhileStopped").Should().Be(0, "切替の後の合わせは止まった判定から出る後ろ向きではない");
        Field(line, "otherBackwardWhileStopped").Should().Be(1, "確定した Jump の後の切替の後ろ向き 1 本は「残り」に数える");
        Field(line, "heldJumpLandings").Should().Be(0);
        (Field(line, "backwardSeeksWhileStopped") + Field(line, "heldJumpBackwardWhileStopped") + Field(line, "otherBackwardWhileStopped"))
            .Should().Be(1, "3 つの和は旧 backwardSeeksWhileStopped（この台本では 1）と一致する");
    }

    /// <summary>停止モードの着地: 追従 → 映像を保持値の 0.5 秒先へ → 保持。止まった値への着地 1 本は今どおり backwardSeeksWhileStopped。</summary>
    [Fact]
    public void StopModeLanding_StaysInBackwardSeeksWhileStopped()
    {
        using var sink = new SummarySink();
        ScenarioClock clock = NewClock();
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true) { SignalLossMode = LtcSignalLossMode.Stop };
        h.AddTrack("A", 0, 120);
        h.ManualPlay();
        h.AdvancePlayback(10.0);
        h.Ltc.Normal(10.0, TimeSpan.FromSeconds(3));
        long holdStart = h.Ltc.NextMilliseconds;
        h.Ltc.Duplicate(13.0, TimeSpan.FromSeconds(2));
        while (clock.MonotonicMilliseconds < holdStart)
            h.AdvanceMilliseconds(40);
        h.AdvancePlayback(h.PlaybackSeconds + 0.5);
        RunScript(h, clock);
        string line = Summary(h, sink);

        Field(line, "backwardSeeksWhileStopped").Should().Be(1, "停止モードの止まった値への着地は今どおり 1");
        Field(line, "heldJumpLandings").Should().Be(0);
        Field(line, "otherBackwardWhileStopped").Should().Be(0);
    }

    /// <summary>
    /// (C) の印が残らないこと（TSP-Fable の追加）: 損失中に Duplicate で確定した Jump の先（8.0）に映像が既に居て、(C) の合わせの relocate が
    /// 出ない（停止モードは 1 フレーム以内で着地を省く、RunThrough は許容内で同期のシークを出さない）。その後の無関係な relocate（利用者の
    /// 手動シーク）は heldJumpLandings に数えない。
    /// </summary>
    [Theory]
    [InlineData(LtcSignalLossMode.RunThrough)]
    [InlineData(LtcSignalLossMode.Stop)]
    public void HeldJumpNote_WithoutARelocate_DoesNotLeakIntoTheNextRelocate(LtcSignalLossMode lossMode)
    {
        using var sink = new SummarySink();
        ScenarioClock clock = NewClock();
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true) { SignalLossMode = lossMode };
        h.AddTrack("A", 0, 120);
        h.ManualPlay();
        h.AdvancePlayback(10.0);
        h.Ltc.Normal(10.0, TimeSpan.FromSeconds(3));
        h.Ltc.Duplicate(13.0, TimeSpan.FromSeconds(1));    // 保持（損失）
        long jumpAt = h.Ltc.NextMilliseconds;
        h.Ltc.Duplicate(8.0, TimeSpan.FromSeconds(1));     // 損失中に 8.0 へ飛んで、Duplicate で確定（(C)）
        while (clock.MonotonicMilliseconds < jumpAt)
            h.AdvanceMilliseconds(40);
        h.AdvancePlayback(8.0);                             // 映像は既に 8.0 に居る（(C) の合わせの relocate が出ない）
        int seeksBefore = h.Operations.Count(o => o.Name == "seek");
        RunScript(h, clock);
        h.Operations.Count(o => o.Name == "seek").Should().Be(seeksBefore, "前提: (C) の合わせの relocate は出ない");

        h.BeginSeekBarInteraction();
        h.EndSeekBarInteraction(3.0);                       // 無関係な relocate（利用者の手動シーク）
        string line = Summary(h, sink);

        Field(line, "heldJumpLandings").Should().Be(0, "(C) の印は、relocate が出なかったときもその場で消え、次の無関係な relocate に付かない");
    }
}
