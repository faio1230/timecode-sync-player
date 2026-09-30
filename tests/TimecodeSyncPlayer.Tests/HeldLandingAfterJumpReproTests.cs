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
        // D35 の失敗帯（許容内の行き過ぎ）でも明示着地する位置。v0.5.4 B6b: 最初の Normal の時点で
        // 再生は 0.10 秒進むので、その時点の行き過ぎが 0.18 秒（許容内）になるよう 20.08 から始める
        // （旧の 20.18 は 0.28 秒で許容外になり、学習前の閾値が tol になった後は同期シークが 1 本増える）。
        h.AdvancePlayback(20.08);
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

        // v0.5.4 B7: Jump はすべて次の 1 フレームの値の連続性で確かめる（保持損失中の復帰も確認の後）。
        h.Ltc.Jump(23.0).Normal(23.04, OneFrame);   // 次の Tick で Jump と確認の 1 フレームが届き、D27-b の復帰
        h.Tick100Milliseconds();
        h.IsPaused.Should().BeFalse("前提: Jump と確認の 1 フレームで復帰する");
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
        // v0.5.4 B6b（追補 3 で期待を変更）: 旧は「シーク 0 本」。U8 の 2 枚規則を Jump の後に限らなく
        // したので、最初の保持の停止が 250ms を待たずに確定し、復帰の Jump（23.0）の同期要求はゲート（13）で
        // 保留されて無音の最初の Tick に出る（目標は新しい値 23.0 + c）。§6 の 4 の欠陥は「古い保持値
        // （20.0）へ着地する」ことなので、その主張に絞る（隣の Normal のテストと同じ形）。
        SeekTargets(h).Should().NotContain(target => Math.Abs(target - 20.0) < 0.1,
            "Jump の復帰で保持値 2 つが下りていれば、無音の損失は古い保持値へ着地しない（§6 の 4 の意図）");
    }

    [Fact]
    public void HeldLossJumpRecovery_ThenNormalFrames_ThenSilentLoss_DoesNotLandOnTheOldHeldValue()
    {
        SyncScenarioHarness h = ArrangeRecoveredFromHeldLoss();

        h.Ltc.Normal(23.08, OneFrame).Normal(23.12, OneFrame).Normal(23.16, OneFrame);
        h.Tick100Milliseconds();    // Normal が届く
        h.Tick100Milliseconds(3);   // 無音の 2 度目の損失

        h.IsPaused.Should().BeTrue("2 度目の無音損失で停止する");
        // §6 の 4 の欠陥は「古い保持値（20.0）へ着地する」こと。現在の LTC（23.12）への粗いシークは
        // 欠陥ではない（v0.5.4 段 B で着地の時機が変わり、D37-a のゲートが Normal 3 枚で開く場面）。
        SeekTargets(h).Should().NotContain(target => Math.Abs(target - 20.0) < 0.1,
            "Normal で保持値が明けているため、古い保持値へ着地しない");
    }
}
