using FluentAssertions;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.4 段 U8: 停止モードで Jump の直後に Duplicate（保持）が 2 枚続いたら、損失の確定
/// （250ms、門 1）を待たずに一時停止する（設計: docs/design/v0.5.4-gate-unification.md §4・§5、
/// docs/design/v0.5.3-d38-seek-gates.md §8。利用者の決定 2026-09-26）。
/// 250ms の待ちは「無音と保持の区別が付いていない場合」だけに残す。1 枚だけなら数えない
/// （発生器の合わせ直しで Jump の直後に同値が 1 枚挟まり、すぐ進み直す場合にカクつかせない）。
/// </summary>
public sealed class StopModeJumpImmediatePauseTests
{
    private static readonly TimeSpan OneFrame = TimeSpan.FromMilliseconds(40);

    private static SyncScenarioHarness Arrange()
    {
        var clock = new ScenarioClock(new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.Stop,
            GapBehavior = GapBehavior.Black,
        };
        h.AddTrack("A", 0, 30);
        h.ReloadProject();
        h.SetDurationSeconds(30);
        h.ManualPlay();
        h.AdvancePlayback(20.18);
        return h;
    }

    /// <summary>
    /// 前提: 保持の損失で停止 → 値が動いた Jump と確認の 1 フレームで復帰（D27-b。v0.5.4 B7 で確認の後）、まで進める。
    /// 復帰後は再生中で、進行の時計は Jump の到着時刻にリセットされている。
    /// </summary>
    private static SyncScenarioHarness ArrangeAfterJumpRecovery()
    {
        SyncScenarioHarness h = Arrange();
        h.Ltc.Normal(20.0, OneFrame)
            .Duplicate(20.0, TimeSpan.FromMilliseconds(300));
        h.Tick100Milliseconds(3);
        h.IsPaused.Should().BeTrue("前提: 保持の損失で停止している");
        h.Operations.Clear();

        // v0.5.4 B7: Jump はすべて次の 1 フレームの値の連続性で確かめる（保持損失中の復帰も確認の後）。
        h.Ltc.Jump(23.0).Normal(23.04, OneFrame);
        h.Tick100Milliseconds();
        h.IsPaused.Should().BeFalse("前提: Jump と確認の 1 フレームで復帰する");
        h.Operations.Clear();
        return h;
    }

    [Fact]
    public void StopMode_JumpThenTwoDuplicates_PausesWithoutWaitingTheConfirmationTimeout()
    {
        SyncScenarioHarness h = ArrangeAfterJumpRecovery();

        // Jump の後に保持の Duplicate が 2 枚続く（40ms 間隔）。250ms の確認は待たない。
        h.Ltc.Duplicate(23.04, TimeSpan.FromMilliseconds(80));
        h.Tick100Milliseconds();

        h.IsPaused.Should().BeTrue(
            "停止モードでは Jump の直後に保持が 2 枚続いたら、損失の確定（250ms、門 1）を待たずに一時停止する（U8）");
    }

    [Fact]
    public void StopMode_JumpThenSingleDuplicateThenNormal_DoesNotPause()
    {
        SyncScenarioHarness h = ArrangeAfterJumpRecovery();

        // 発生器の合わせ直しなどで、Jump の直後に同値が 1 枚だけ挟まり、また進み直す。
        // 1 枚目と Normal を別の Tick に置き、1 枚の時点で判定される形にする。
        long at = h.Ltc.NextMilliseconds;
        h.Ltc.Duplicate(23.04, OneFrame, atMilliseconds: at + 60);
        h.Ltc.Normal(23.08, OneFrame, atMilliseconds: at + 160);

        h.Tick100Milliseconds();    // 保持 1 枚だけが届く
        h.IsPaused.Should().BeFalse(
            "Jump の直後の保持 1 枚では即時停止しない（止まってすぐ動き出すカクつきを避ける）");

        h.Tick100Milliseconds();    // 値が進み直す
        h.IsPaused.Should().BeFalse();
        h.Operations.Should().NotContain(o => o.Name == "signal-loss-pause");
    }
}
