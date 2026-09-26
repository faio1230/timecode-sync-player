using System.Diagnostics;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 C1: ScenarioClock を渡した harness が 1 つの時間軸で動くこと。
/// - 信号断の確定（単調ミリ秒。SupplyLtc と Tick が同じ軸を使う）
/// - サンプル時計の age（QPC）
/// - Tick で UTC・単調ミリ秒・QPC が同時に進む
/// </summary>
public class ScenarioClockHarnessTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SignalLossTimeout_IsTimedByTheScenarioClockTimeline()
    {
        var clock = new ScenarioClock(BaseUtc, monotonicMilliseconds: 50_000);
        var h = new SyncScenarioHarness(scenarioClock: clock);
        h.AddTrack("track-1", timelineIn: 0);
        h.ManualPlay();

        h.SupplyLtc(1);                     // 50_000 に有効フレーム
        h.Tick100Milliseconds(2);           // 50_100 / 50_200: 250ms 未満
        h.IsPaused.Should().BeFalse("250ms の確定前は止まらない");

        h.Tick100Milliseconds();            // 50_300: 250ms 超
        h.IsPaused.Should().BeTrue("無音の信号断は仮想時計の 250ms で確定する");

        clock.MonotonicMilliseconds.Should().Be(50_300, "Tick は ScenarioClock を進める");
        clock.GetUtcNow().Should().Be(BaseUtc.AddMilliseconds(300), "UTC も同じだけ進む");
        clock.Qpc.Should().Be(1_000_000 + (50_300 * Stopwatch.Frequency) / 1000, "QPC も同じ軸");
    }

    [Fact]
    public void SampleClockAge_UsesTheScenarioClockQpc()
    {
        var clock = new ScenarioClock(BaseUtc, monotonicMilliseconds: 50_000, qpcBase: 7_000_000);
        var h = new SyncScenarioHarness(scenarioClock: clock, sampleClockEnabled: true);
        h.AddTrack("track", 0, duration: 5);
        h.ManualPlay();
        h.ChangeMode(SyncMode.Single);
        // 既定のシーク所要 1.0 秒以内の不足だと速度補正優先でシークが出ない（D37-b）。
        // age 込みの不足が 1.0 秒を超える位置から始める（0.5 → 2.0）。
        h.AdvancePlayback(0.5, renderedFrames: 2);
        h.Operations.Clear();

        // フレーム終端から 200ms 後に受信したフレームとして渡す（age = 0.2 秒）。
        long frameEnd = clock.Qpc - (200 * Stopwatch.Frequency / 1000);
        h.SupplyLtcFrame(1.8, frameEnd, clock.Qpc);

        h.Operations.Should().ContainSingle(operation => operation.Name == "seek")
            .Which.Value.Should().BeApproximately(2.0, 1e-9,
                "サンプル時計の age は ScenarioClock の QPC から取る（1.8 + 0.2）。" +
                "時計が配線されていなければ 1.8 のままになる");
    }

    [Fact]
    public void ScenarioClockWithTimeProviderOrGetQpc_IsRejected()
    {
        var clock = new ScenarioClock(BaseUtc);
        var manual = new ManualTimeProvider(BaseUtc);

        Action withTimeProvider = () => _ = new SyncScenarioHarness(manual, scenarioClock: clock);
        Action withGetQpc = () => _ = new SyncScenarioHarness(
            scenarioClock: clock, getQpc: () => clock.Qpc);

        withTimeProvider.Should().Throw<ArgumentException>();
        withGetQpc.Should().Throw<ArgumentException>();
    }
}
