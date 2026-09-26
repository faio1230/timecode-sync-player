using FluentAssertions;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.3 段 3j: §6 の 4 の再現。保持（Duplicate）が理由の損失中に値が動いた Jump 1 枚で
/// 即時復帰（D27-b）しても、保持値 2 つ（LastHeldEffective / HeldLossLanding）は下りず、
/// Jump の直後に再び無音で損失すると古い保持値へ着地していた（段 0 の表は Keeps・意図 Clear）。
/// v0.5.3 段 3k で直した（Jump の即時復帰で保持値 2 つを下ろす）。
/// v0.5.4 C3: 入力は LtcScript（Normal／Duplicate／Jump／無音）で仮想時計から流す。
/// </summary>
public sealed class HeldLandingAfterJumpReproTests
{
    private static readonly TimeSpan OneFrame = TimeSpan.FromMilliseconds(40);

    private static SyncScenarioHarness Arrange()
    {
        var clock = new ScenarioClock(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.Stop,
            GapBehavior = GapBehavior.Black,
        };
        h.AddTrack("A", 0, 30);
        h.ReloadProject();
        h.SetDurationSeconds(30);
        h.ManualPlay();
        h.AdvancePlayback(20.18);   // D35 の失敗帯（許容内の行き過ぎ）でも明示着地する位置
        return h;
    }

    private static IReadOnlyList<double> SeekTargets(SyncScenarioHarness h) =>
        h.Operations.Where(o => o.Name == "seek").Select(o => o.Value ?? double.NaN).ToList();

    /// <summary>保持値 20.0 で停止・着地した後、値が動いた Jump 23.0 の 1 枚で復帰したところまで進める。</summary>
    private static SyncScenarioHarness ArrangeRecoveredFromHeldLoss()
    {
        SyncScenarioHarness h = Arrange();
        h.Ltc.Normal(20.0, OneFrame)
            .Duplicate(20.0, TimeSpan.FromMilliseconds(300));
        h.Tick100Milliseconds(3);   // 保持の損失で一時停止し、保持値 20.0 へ着地
        h.IsPaused.Should().BeTrue("前提: 保持の損失で停止している");
        SeekTargets(h).Should().Equal(new[] { 20.0 }, "前提: 保持値 20.0 へ着地した");
        h.Operations.Clear();

        h.Ltc.Jump(23.0);          // 次の Tick で Jump が届き、D27-b の即時復帰
        h.Tick100Milliseconds();
        h.IsPaused.Should().BeFalse("前提: Jump で即時復帰する");
        h.Operations.Clear();      // 復帰時のシーク（23.0）は見ない
        return h;
    }

    [Fact]
    public void HeldLossJumpRecovery_ThenSilentLoss_DoesNotLandOnTheOldHeldValue()
    {
        SyncScenarioHarness h = ArrangeRecoveredFromHeldLoss();

        // 追加の LTC は無し（無音）。3 回目の Tick までに無音の損失が確定する。
        h.Tick100Milliseconds(3);

        h.IsPaused.Should().BeTrue("2 度目の無音損失で停止する");
        SeekTargets(h).Should().BeEmpty(
            "Jump の復帰で保持値 2 つが下りていれば、無音の損失は古い保持値へ着地しない（§6 の 4 の意図）");
    }

    [Fact]
    public void HeldLossJumpRecovery_ThenNormalFrames_ThenSilentLoss_DoesNotLandOnTheOldHeldValue()
    {
        SyncScenarioHarness h = ArrangeRecoveredFromHeldLoss();

        h.Ltc.Normal(23.04, OneFrame).Normal(23.08, OneFrame).Normal(23.12, OneFrame);
        h.Tick100Milliseconds();    // Normal が届く
        h.Tick100Milliseconds(3);   // 無音の 2 度目の損失

        h.IsPaused.Should().BeTrue("2 度目の無音損失で停止する");
        SeekTargets(h).Should().BeEmpty("Normal で保持値が明けているため、古い保持値へ着地しない");
    }
}
