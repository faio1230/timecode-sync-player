using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 B7（Jump の確認の一様化、設計 §10-0）: Jump はすべて次の 1 フレームの値の連続性だけで確かめる
/// （写像のトラック・ギャップ・範囲外や、保持損失中かで分けない）。D30/D31 が守った欠陥（誤デコード 1 枚で
/// 別トラックを読み込む・シークする）が、一様の確認でも起きないことを固定する。
/// </summary>
[Collection("Serilog global logger")]
public class B7JumpConfirmationUniformTests
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OneFrame = TimeSpan.FromMilliseconds(40);

    private static (SyncScenarioHarness Harness, ScenarioClock Clock) Arrange(SyncMode mode, LtcSignalLossMode lossMode)
    {
        var clock = new ScenarioClock(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero), monotonicMilliseconds: 50_000);
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = lossMode,
            GapBehavior = GapBehavior.Black,
        };
        h.AddTrack("A", 0, 20);
        h.AddTrack("B", 30, 20);
        if (mode == SyncMode.Single)
        {
            h.ReloadProject();                               // Single は選択中のトラック（A）を読み込んでおく
            h.ChangeMode(SyncMode.Single);
            h.SetDurationSeconds(20);
        }
        h.ManualPlay();
        h.AdvancePlayback(5.0);
        return (h, clock);
    }

    private static void RunToEnd(SyncScenarioHarness h, ScenarioClock clock)
    {
        long end = h.Ltc.NextMilliseconds + 400;
        while (clock.MonotonicMilliseconds < end)
            h.AdvanceMilliseconds(40);
    }

    [Theory]
    [InlineData(SyncMode.Continue, 35.0)]   // 別トラック（B）へ写る壊れた 1 枚
    [InlineData(SyncMode.Continue, 25.0)]   // ギャップへ写る壊れた 1 枚
    [InlineData(SyncMode.Continue, 12.0)]   // 同じトラックの中の壊れた 1 枚
    [InlineData(SyncMode.Single, 15.0)]     // Single の壊れた 1 枚
    public void OneMisdecodedFrame_ThenTheSeriesContinues_NoLoadAndNoSeek(SyncMode mode, double brokenSeconds)
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(mode, LtcSignalLossMode.RunThrough);
        h.Ltc.Normal(5.0, OneSecond);                  // 5.00〜5.96 で追従
        RunToEnd(h, clock);
        h.Operations.Clear();

        h.Ltc.Jump(brokenSeconds);                     // 壊れた 1 枚
        h.Ltc.Normal(6.04, OneSecond);                 // 元の列に戻る
        RunToEnd(h, clock);

        h.Operations.Should().NotContain(o => o.Name == "loadfile" || o.Name == "loadfile-paused",
            "誤デコード 1 枚で別トラック・ギャップを読み込まない（D30）");
        h.Operations.Should().NotContain(o => o.Name == "seek", "誤デコード 1 枚でシークしない");
    }

    [Theory]
    [InlineData(SyncMode.Continue, 35.0)]
    [InlineData(SyncMode.Single, 15.0)]
    public void OneMisdecodedFrameDuringAHeldLoss_DoesNotRecoverOrLoad(SyncMode mode, double brokenSeconds)
    {
        // 保持損失中（停止モードで一時停止）の壊れた 1 枚でも、復帰も読み込みもしない（保持損失中も一様に確認する）。
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(mode, LtcSignalLossMode.Stop);
        h.Ltc.Normal(5.0, OneSecond).Duplicate(5.96, OneSecond);
        RunToEnd(h, clock);
        h.IsPaused.Should().BeTrue("前提: 保持の損失で停止している");
        h.Operations.Clear();

        h.Ltc.Jump(brokenSeconds);                     // 壊れた 1 枚
        h.Ltc.Duplicate(5.96, OneSecond);              // 元の保持に戻る
        RunToEnd(h, clock);

        h.IsPaused.Should().BeTrue("未確認の 1 枚では保持損失から復帰しない");
        h.Operations.Should().NotContain(o => o.Name == "signal-loss-resume" || o.Name == "loadfile",
            "誤デコード 1 枚で復帰も読み込みもしない（D30）");
    }
}
