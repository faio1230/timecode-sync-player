using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D35: 停止モードで保持に入って一時停止したら、同期の tolerance を経由せず保持値へ
/// 明示的に 1 回着地する（|位置 − 保持値| が 1 フレーム以内なら省略）。損失理由
/// （SignalLoss / TimecodeHeld）や保持値が損失宣言の前後どちらで分かったかに依らない。
/// </summary>
public sealed class StopModeHeldLandingTests
{
    private static void Raw(SyncScenarioHarness h, int seconds, int frame = 0, long at = 10_000) =>
        h.Controller.ReceiveFrame(
            new(new LtcTimecode(0, seconds / 60, seconds % 60, frame, false), 25, seconds + frame / 25d), at);

    private static (SyncScenarioHarness h, ManualTimeProvider clock) Arrange(
        double playbackSeconds, double acceptedSeconds = 12.0)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true) { SignalLossMode = LtcSignalLossMode.Stop };
        h.AddTrack("first", 0, 30);
        h.ChangeMode(SyncMode.Single);
        h.SetDurationSeconds(20);
        h.ManualPlay();
        h.AdvancePlayback(playbackSeconds, 5);
        Raw(h, (int)acceptedSeconds);
        h.Operations.Clear();
        return (h, clock);
    }

    /// <summary>保持フレームを供給しつつ有効フレームの時計を進め、timeout（250ms）超えで損失を確定させる。</summary>
    private static void HoldPastTimeout(SyncScenarioHarness h, double heldSeconds)
    {
        h.SupplyHeldLtc(heldSeconds);
        h.Tick100Milliseconds();
        h.SupplyHeldLtc(heldSeconds);
        h.Tick100Milliseconds();
        h.SupplyHeldLtc(heldSeconds);
        h.Tick100Milliseconds();
    }

    private static IReadOnlyList<double> SeekTargets(SyncScenarioHarness h) =>
        h.Operations.Where(o => o.Name == "seek").Select(o => o.Value ?? double.NaN).ToList();

    [Fact]
    public void StopMode_TimecodeHeld_WithBandOvershoot_LandsExplicitly()
    {
        // R-1 の失敗帯: 行き過ぎ 0.18 秒は同期の tolerance 0.240 秒以内なので、明示着地が
        // 無いと Seek 判定にならず行き過ぎが残る。
        (SyncScenarioHarness h, _) = Arrange(playbackSeconds: 12.18);

        HoldPastTimeout(h, 12.0);

        h.IsPaused.Should().BeTrue("保持の検出で一時停止する");
        SeekTargets(h).Should().ContainSingle("許容内の行き過ぎでも保持値へ明示的に着地する")
            .Which.Should().BeApproximately(12.0, 0.001);
    }

    [Fact]
    public void StopMode_TimecodeHeld_WithLargeOvershoot_LandsOnce()
    {
        (SyncScenarioHarness h, _) = Arrange(playbackSeconds: 12.0);
        h.AdvancePlayback(12.55, 5); // 保持中に再生が 0.55 秒先行した状態

        HoldPastTimeout(h, 12.0);

        h.IsPaused.Should().BeTrue();
        SeekTargets(h).Should().Equal(new[] { 12.0 });
    }

    [Fact]
    public void StopMode_SilentLoss_ThenHeldDuplicate_LandsOnce()
    {
        // ケース 1: 無音損失で保持値が無いまま一時停止し、その後に届いた Duplicate で
        // 初めて保持値が分かる。損失理由が SignalLoss でも値が分かった時点で着地する。
        (SyncScenarioHarness h, _) = Arrange(playbackSeconds: 12.0);
        h.AdvancePlayback(12.18, 5);

        h.Tick100Milliseconds();
        h.Tick100Milliseconds();
        h.Tick100Milliseconds();

        h.IsPaused.Should().BeTrue();
        SeekTargets(h).Should().BeEmpty("無音では着地先の保持値が無い");

        h.SupplyHeldLtc(12.0);
        SeekTargets(h).Should().ContainSingle("保持値が分かった時点で 1 回着地する")
            .Which.Should().BeApproximately(12.0, 0.001);

        h.SupplyHeldLtc(12.0);
        SeekTargets(h).Should().ContainSingle("同じ保持値の連続では着地を繰り返さない");
    }

    [Fact]
    public void StopMode_HeldWithinOneFrame_DoesNotLand()
    {
        (SyncScenarioHarness h, _) = Arrange(playbackSeconds: 12.01);

        HoldPastTimeout(h, 12.0);

        h.IsPaused.Should().BeTrue();
        SeekTargets(h).Should().BeEmpty("1 フレーム以内なら既に保持位置なので省略する");
    }

    [Fact]
    public void StopMode_Pause_RestoresSmoothRateTo1()
    {
        // 行き過ぎ 0.20 秒（許容内）で Smooth の 0.9 倍が残った状態から保持の一時停止に入る。
        // pause の前に rate 1.0 へ戻す。
        (SyncScenarioHarness h, _) = Arrange(playbackSeconds: 12.2);

        h.AppliedRates.Should().NotBeEmpty("許容内の残差は Smooth の速度補正で詰める");
        h.AppliedRates[^1].Should().NotBe(1.0);

        HoldPastTimeout(h, 12.0);

        h.IsPaused.Should().BeTrue();
        h.AppliedRates[^1].Should().Be(1.0, "停止モードでは pause の直前に倍率を 1.0 に戻す");
        SeekTargets(h).Should().Equal(new[] { 12.0 });
    }
}
