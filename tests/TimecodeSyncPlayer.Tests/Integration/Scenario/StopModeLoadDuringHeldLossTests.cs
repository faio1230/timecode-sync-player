using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4（S-4 の報告の未解決 2、規則 4 の実装の抜け）: 停止モードで保持による損失のまま次のトラックを読み込むと、
/// 読み込みが再生を始め、損失のまま（EvaluatePause がもう一度来ない）なので 0 から走り続けていた。
/// 読み込みでも規則 4 の入口と同じく一時停止し（持ち主 = 信号断）、保持値へ着地し、復帰（新しい値のフレーム）で
/// 規則 2〜3 に戻る。
/// </summary>
[Collection("Serilog global logger")]
public class StopModeLoadDuringHeldLossTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private static ScenarioClock NewClock() =>
        new(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero), monotonicMilliseconds: 50_000);

    private static void RunFor(SyncScenarioHarness h, ScenarioClock clock, int milliseconds)
    {
        long end = clock.MonotonicMilliseconds + milliseconds;
        while (clock.MonotonicMilliseconds < end)
            h.AdvanceMilliseconds(40);
    }

    private static IReadOnlyList<double> Seeks(SyncScenarioHarness h) =>
        h.Operations.Where(o => o.Name == "seek").Select(o => o.Value ?? double.NaN).ToList();

    [Fact]
    public void Single_ManualLoadDuringAHeldLoss_StaysPausedByTheSignalLoss_LandsOnTheHeldValue_ThenFollowsOnRecovery()
    {
        ScenarioClock clock = NewClock();
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.Stop,
        };
        h.AddTrack("A", 0, 20);
        h.AddTrack("B", 30, 20);
        h.ReloadProject();
        h.ChangeMode(SyncMode.Single);
        h.SetDurationSeconds(20);
        h.ManualPlay();
        h.AdvancePlayback(5.0);

        // 5.00〜5.96 で追従し、10.0 で保持（停止モードで一時停止）。その後 12.0 から進む（復帰）。
        h.Ltc.Normal(5.0, OneSecond).Jump(10.0).Duplicate(10.0, TimeSpan.FromSeconds(3)).Normal(12.0, TimeSpan.FromSeconds(2));
        RunFor(h, clock, 2_000);
        h.IsPaused.Should().BeTrue("前提: 保持の損失で停止している");
        h.Controller.IsSignalLossPauseOwned.Should().BeTrue("前提: 持ち主は信号断");

        // 保持の損失のまま、手動で次のトラックを読み込む（アプリは位置なしで読み込み、再生を始め、同期の読み込みの入口を通す）。
        h.ManualNextTrack();
        h.BeginManualFileLoad();
        h.Operations.Clear();
        RunFor(h, clock, 1_000);

        h.IsPaused.Should().BeTrue("保持の損失のままの読み込みでも、規則 4 どおり停止モードでは一時停止する");
        h.Controller.IsSignalLossPauseOwned.Should().BeTrue("一時停止の持ち主は信号断（復帰で再開できる）");
        Seeks(h).Should().ContainSingle("新しいトラックでも保持値へ 1 回だけ着地する")
            .Which.Should().BeApproximately(10.0, 1e-6);
        double heldPosition = h.PlaybackSeconds;
        RunFor(h, clock, 500);
        h.PlaybackSeconds.Should().Be(heldPosition, "保持の間は走らない");

        // 復帰（12.0 から進む値のフレーム）で再開し、規則 2〜3 に戻る（新しい値へ relocate）。
        RunFor(h, clock, 1_500);
        h.Operations.Should().Contain(o => o.Name == "signal-loss-resume", "新しい値のフレームで信号断の一時停止を解く");
        h.IsPaused.Should().BeFalse();
        Seeks(h).Should().Contain(s => s > 12.0 - 0.1 && s < 14.0, "復帰した値へ relocate する（規則 3）");
    }

    [Fact]
    public void Continue_MasterStopsDuringATrackSwitch_PausesByTheSignalLoss_OnTheHeldValue()
    {
        ScenarioClock clock = NewClock();
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.Stop,
        };
        h.AddTrack("A", 0, 10);
        h.AddTrack("B", 10, 10);
        h.ManualPlay();
        h.AdvancePlayback(8.0);

        // 8.00 から進み、10.0 で B へ切り替わった直後に 10.20 で保持に入る。
        h.Ltc.Normal(8.0, TimeSpan.FromMilliseconds(2_200)).Duplicate(10.2, TimeSpan.FromSeconds(3));
        RunFor(h, clock, 2_120);
        h.Operations.Should().Contain(o => o.Name == "loadfile", "前提: B への切替の読み込み");
        h.Operations.Clear();

        RunFor(h, clock, 1_000);
        h.IsPaused.Should().BeTrue("切替の途中でマスターが止まっても、停止モードでは一時停止する");
        h.Controller.IsSignalLossPauseOwned.Should().BeTrue("一時停止の持ち主は信号断");
        h.PlaybackSeconds.Should().BeApproximately(0.2, 0.05, "B の中の保持値の位置で止まる");
        double heldPosition = h.PlaybackSeconds;
        RunFor(h, clock, 500);
        h.PlaybackSeconds.Should().Be(heldPosition, "保持の間は走らない");
    }
}
