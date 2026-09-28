using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// S-4 の型（v0.5.4 B6b の回帰、docs/reports/2026-09-28-agent-b-v054-S4-stale.md）: 読み込みの直後、尺がまだ
/// 分からない（アプリは読み込みで尺を 0 に戻し、UI タイマーが取れたときに入れる）うちに、規則 4 の保持値への合わせ
/// （ランスルーの入口・停止モードの保持の着地）が目標を決めると、クリップの端が素材の終わりちょうど（MediaOut）になり、
/// そこへシークして EOF に入る。尺と fps が分かるまでは判定せず、分かった後の最初の保持のフレームで 1 回だけ、
/// 最後のコマの頭へ合わせる。
/// </summary>
public class S4HeldLandingBeforeDurationTests
{
    private static readonly TimeSpan OneFrame = TimeSpan.FromMilliseconds(40);

    private static (SyncScenarioHarness Harness, ScenarioClock Clock) Arrange(LtcSignalLossMode lossMode)
    {
        var clock = new ScenarioClock(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero), monotonicMilliseconds: 50_000);
        // S-4 の素材と同じく MediaOut 00:00:20（尺と同じ）を持つトラック。
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = lossMode,
            MediaOutSeconds = 20.0,
        };
        h.AddTrack("A", 5, 20);
        h.AddTrack("B", 30, 20);
        h.ChangeMode(SyncMode.Single);
        h.SetDurationSeconds(20);
        h.ManualPlay();
        h.AdvancePlayback(1.0);
        return (h, clock);
    }

    private static void RunFor(SyncScenarioHarness h, ScenarioClock clock, int milliseconds)
    {
        long end = clock.MonotonicMilliseconds + milliseconds;
        while (clock.MonotonicMilliseconds < end)
            h.AdvanceMilliseconds(40);
    }

    private static IReadOnlyList<double> Seeks(SyncScenarioHarness h) =>
        h.Operations.Where(o => o.Name == "seek").Select(o => o.Value ?? double.NaN).ToList();

    [Fact]
    public void RunThroughHoldEntry_AfterALoad_WaitsForTheDuration_ThenAlignsOnceToTheLastFrame()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(LtcSignalLossMode.RunThrough);
        // LTC はクリップの外（35.0）で保持（S-4 と同じ）。
        h.Ltc.Normal(34.8, TimeSpan.FromMilliseconds(200)).Duplicate(35.0, TimeSpan.FromSeconds(6));
        RunFor(h, clock, 1_500);

        // 保持の中で次のトラックを読み込む。尺は 0.3 秒後に分かる。
        h.Playback.DurationArrivalDelaySeconds = 0.3;
        h.ManualNextTrack();
        h.Operations.Clear();

        RunFor(h, clock, 200);   // 尺がまだ分からない間に保持の Duplicate が 5 枚届く
        Seeks(h).Should().BeEmpty("尺と fps が分からない間は、入口の合わせの目標を決めない（素材の終わりちょうどへシークしない）");

        RunFor(h, clock, 1_000);  // 尺が分かった後の保持のフレーム
        double lastFrameStart = 20.0 - (1.0 / h.Playback.Fps);
        Seeks(h).Should().ContainSingle("尺が分かった後の最初の保持のフレームで 1 回だけ合わせる")
            .Which.Should().BeApproximately(lastFrameStart, 1e-6, "最後のコマの頭（素材の終わりちょうどではない）");
    }

    [Fact]
    public void StopModeHeldLanding_AfterALoad_WaitsForTheDuration_ThenLandsOnceOnTheLastFrame()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(LtcSignalLossMode.Stop);
        // LTC はクリップの外で進み、次のトラックを読み込んだ直後に保持へ入る（U8 の即時停止で保持値へ着地する）。
        h.Ltc.Normal(33.4, TimeSpan.FromMilliseconds(1_600)).Duplicate(35.04, TimeSpan.FromSeconds(3));
        RunFor(h, clock, 1_500);

        h.Playback.DurationArrivalDelaySeconds = 0.5;
        h.ManualNextTrack();
        h.Operations.Clear();

        RunFor(h, clock, 300);   // 保持に入り停止するが、尺はまだ分からない
        h.IsPaused.Should().BeTrue("前提: 保持で停止した");
        Seeks(h).Should().BeEmpty("尺と fps が分からない間は、保持値への着地の目標を決めない（素材の終わりちょうどへシークしない）");

        RunFor(h, clock, 1_000);  // 尺が分かった後の保持のフレーム
        double lastFrameStart = 20.0 - (1.0 / h.Playback.Fps);
        Seeks(h).Should().ContainSingle("尺が分かった後の最初の保持のフレームで 1 回だけ着地する")
            .Which.Should().BeApproximately(lastFrameStart, 1e-6, "最後のコマの頭（素材の終わりちょうどではない）");
    }
}
