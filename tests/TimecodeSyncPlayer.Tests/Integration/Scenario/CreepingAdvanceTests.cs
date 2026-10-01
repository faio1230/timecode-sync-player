using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.6.1 3-5 節の (iii) 這う前進（レビューの直し、TSP-Fable の判断）: 受理済みの値から +1〜数フレームだけ、実時間より遅れて
/// 進んだ値。A と時刻を付け直すが、M は止める（外挿しない）。合わせはモードで分ける。
/// - 停止モードで信号断が止めている間: 止めたまま新しい保持値へ 1 回着地する（1 フレーム精度）。再生は走らない
/// - ランスルー（と止めていない間）: 合わせない
/// - 保持の外の 1 枚（走行中の遅れた 1 枚）は保持着地の記録を立てない（規則 4 の入口の合わせを消さない）
/// </summary>
public class CreepingAdvanceTests
{
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
}
