using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 C3: 台本のフレームが仮想時計に合わせて controller へ届き、無音は
/// 250ms の確定を時計の進みだけで起こせること。
/// </summary>
public class LtcScriptHarnessTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Script_FramesReachTheControllerOnTheVirtualClock()
    {
        var clock = new ScenarioClock(BaseUtc, monotonicMilliseconds: 50_000);
        var h = new SyncScenarioHarness(scenarioClock: clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();

        h.Ltc.Normal(5.0, TimeSpan.FromMilliseconds(200));
        h.Tick100Milliseconds(2);

        h.Controller.LastLtcSeconds.Should().BeApproximately(5.16, 1e-9,
            "200ms ぶんの 5 フレーム（5.00〜5.16）が予定時刻で届く");
    }

    [Fact]
    public void Script_SilenceLetsTheSignalLossTimeoutFire()
    {
        var clock = new ScenarioClock(BaseUtc, monotonicMilliseconds: 50_000);
        var h = new SyncScenarioHarness(scenarioClock: clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();

        h.Ltc.Normal(5.0, TimeSpan.FromMilliseconds(40));
        h.Tick100Milliseconds(2);
        h.IsPaused.Should().BeFalse("最後のフレームから 200ms ではまだ確定しない");

        h.Tick100Milliseconds();
        h.IsPaused.Should().BeTrue("仮想時計の 250ms で無音の信号断が確定する");
    }
}
