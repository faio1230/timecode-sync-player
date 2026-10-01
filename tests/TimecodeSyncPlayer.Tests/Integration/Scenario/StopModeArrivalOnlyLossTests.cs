using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.6.1 (D) のレビューの直し: 停止モードで U8 の損失（保持）・一時停止の後、同値の Duplicate が途切れて受理しないフレーム
/// （化けた値）だけが続く間は「保持の到着」が無い。理由は「停止」のまま（LTC は来ている）だが、確定した Jump で保持の直後の
/// 即時の復帰（D27-b/c）に入ってはいけない（復帰は有効フレームの数えで）。
/// </summary>
public class StopModeArrivalOnlyLossTests
{
    private const int FrameMs = 33;

    private static SyncScenarioHarness Arrange(ScenarioClock clock)
    {
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.Stop,
            FpsMode = TimecodeFpsMode.Fixed30,
        };
        h.AddTrack("A", 0, 60);
        h.ReloadProject();
        h.SetDurationSeconds(60);
        h.ManualPlay();
        h.AdvancePlayback(9.0);
        return h;
    }

    /// <summary>30fps の番号で timecode を作り、実の受信経路（fps の解決・診断）へ渡す。</summary>
    private static void Frame(SyncScenarioHarness h, ScenarioClock clock, double seconds)
    {
        int frame = (int)Math.Round(seconds * 30.0);
        var timecode = new LtcTimecode(
            frame / (30 * 3600), frame / (30 * 60) % 60, frame / 30 % 60, frame % 30, false);
        h.Controller.ReceiveFrame(
            new LtcFrameReceivedEventArgs(timecode, 30.0, seconds, clock.Qpc, clock.Qpc), clock.MonotonicMilliseconds);
    }

    [Fact]
    public void HeldLossThenOnlyGarbledArrivals_ConfirmedJump_DoesNotResumeImmediately()
    {
        var clock = new ScenarioClock(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), monotonicMilliseconds: 50_000);
        SyncScenarioHarness h = Arrange(clock);

        // 9.000〜10.000 を 30fps で追従し、10.000 で保持（U8 で即時に損失、一時停止）。
        for (int i = 0; i <= 30; i++)
        {
            Frame(h, clock, 9.0 + i / 30.0);
            h.AdvanceMilliseconds(FrameMs);
        }
        for (int i = 0; i < 3; i++)
        {
            Frame(h, clock, 10.0);
            h.AdvanceMilliseconds(FrameMs);
        }
        h.IsPaused.Should().BeTrue("前提: 保持で U8 の損失・一時停止");

        // 同値の Duplicate が途切れ、化けた値だけが 1 秒続く（LTC は来ているが、保持の到着は無い）。
        double[] garbled = [55.5, 3.3, 77.7, 41.1, 0.0, 66.6];
        for (int i = 0; i < 30; i++)
        {
            Frame(h, clock, garbled[i % garbled.Length]);
            h.AdvanceMilliseconds(FrameMs);
        }
        h.Operations.Clear();

        // 別の位置への Jump と、確認の +1 フレーム。
        Frame(h, clock, 20.0);
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 20.0 + 1.0 / 30.0);
        h.AdvanceMilliseconds(FrameMs);

        h.IsPaused.Should().BeTrue("保持の到着が途切れた後の Jump は、保持の直後の即時の復帰に数えない");
        h.Operations.Should().NotContain(o => o.Name == "signal-loss-resume");
    }
}
