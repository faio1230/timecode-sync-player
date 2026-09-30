using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4（候補 4 の 1b、規則 4 の実装）: 同値の Duplicate で確認された Jump は、確認と同時に保持に入っている。
/// 保持中は補正しない（varispeed しない）ので、その適用で倍率を変えない。確認の定義（IsConfirmedBy）は変えない。
/// </summary>
[Collection("Serilog global logger")]
public class ConfirmedJumpIntoHoldTests
{
    private const int FrameMs = 33;

    private static ScenarioClock NewClock() =>
        new(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero), monotonicMilliseconds: 50_000);

    private static void Frame(SyncScenarioHarness h, ScenarioClock clock, double seconds)
    {
        int frame = (int)Math.Round(seconds * 30.0);
        var timecode = new LtcTimecode(
            frame / (30 * 3600), frame / (30 * 60) % 60, frame / 30 % 60, frame % 30, false);
        h.Controller.ReceiveFrame(
            new LtcFrameReceivedEventArgs(timecode, 30.0, seconds, 0, 0), clock.MonotonicMilliseconds);
    }

    [Theory]
    [InlineData(TimecodeFpsMode.Fixed30)]
    [InlineData(TimecodeFpsMode.Auto)]
    public void JumpConfirmedBySameValueDuplicate_DoesNotChangeTheRate(TimecodeFpsMode fpsMode)
    {
        ScenarioClock clock = NewClock();
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.RunThrough,
            FpsMode = fpsMode,
        };
        h.AddTrack("A", 0, 20);
        h.ReloadProject();
        h.SetDurationSeconds(20);
        h.ManualPlay();
        h.AdvancePlayback(7.0);
        for (int i = 0; i <= 30; i++)
        {
            Frame(h, clock, 7.0 + i / 30.0);
            h.AdvanceMilliseconds(FrameMs);
        }
        h.Operations.Clear();

        // 許容の中の値（8.2。位置は約 8.07）へ飛んで、同値の Duplicate で確認される（確認と同時に保持に入る）。
        Frame(h, clock, 8.2);
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 8.2);

        h.Operations.Should().NotContain(o => o.Name == "seek", "前提: 許容の中なので relocate しない");
        h.Operations.Should().NotContain(o => o.Name == "rate" && o.Value != 1.0,
            "保持に入った確認の適用では補正しない（倍率を変えない）");
    }

    [Fact]
    public void JumpConfirmedBySameValueDuplicate_RestoresARunningVarispeedToOne()
    {
        ScenarioClock clock = NewClock();
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.RunThrough,
            FpsMode = TimecodeFpsMode.Fixed30,
        };
        h.AddTrack("A", 0, 20);
        h.ReloadProject();
        h.SetDurationSeconds(20);
        h.ManualPlay();
        // 位置を LTC より 0.1 秒遅らせて追従させ、varispeed（1 以外の倍率）を掛けた状態にする。
        h.AdvancePlayback(6.9);
        for (int i = 0; i <= 30; i++)
        {
            Frame(h, clock, 7.0 + i / 30.0);
            h.AdvanceMilliseconds(FrameMs);
        }
        h.AppliedRates.Should().NotBeEmpty();
        h.AppliedRates[^1].Should().NotBe(1.0, "前提: varispeed が掛かっている");

        Frame(h, clock, 8.2);
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 8.2);

        h.AppliedRates[^1].Should().Be(1.0, "保持に入った確認の適用では、掛かっていた倍率を 1.0 に戻す（RunThrough は 1.0 で走る）");
    }
}
