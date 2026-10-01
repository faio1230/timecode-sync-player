using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4（マスター停止の判定の共通化、検証機の候補 1 の A 切替の 2 回目）: 化けた 1 枚（Fixed30 で検出 24fps）の
/// Duplicate が保持値の記録を立て、「マスター停止」と判定されて relocate の先行量が 0 になっていた。マスター停止は
/// 数える保持の連続（fps の疑わしい Duplicate は数えない、連続の同値）で決める。先行量 0 は 1 枚、入口は 2 枚。
/// </summary>
[Collection("Serilog global logger")]
public class MasterStoppedDefinitionTests
{
    private const int FrameMs = 33;
    private const double SeekCost = 0.3;

    private static SyncScenarioHarness Arrange(ScenarioClock clock, TimecodeFpsMode fpsMode)
    {
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.RunThrough,
            FpsMode = fpsMode,
        };
        h.AddTrack("A", 0, 20);
        h.AddTrack("B", 30, 20);
        h.ReloadProject();
        h.SetDurationSeconds(20);
        h.ManualPlay();
        h.AdvancePlayback(7.0);
        // シークの所要のヒント（学習前の c）。マスターが動いている間の relocate の先行量になる。
        h.SyncService.SetSeekCostHintSeconds(SeekCost);
        return h;
    }

    private static ScenarioClock NewClock() =>
        new(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero), monotonicMilliseconds: 50_000);

    private static void Frame(SyncScenarioHarness h, ScenarioClock clock, double seconds, double detectedFps = 30.0)
    {
        int frame = (int)Math.Round(seconds * 30.0);
        var timecode = new LtcTimecode(
            frame / (30 * 3600), frame / (30 * 60) % 60, frame / 30 % 60, frame % 30, false);
        h.Controller.ReceiveFrame(
            new LtcFrameReceivedEventArgs(timecode, detectedFps, seconds, 0, 0), clock.MonotonicMilliseconds);
    }

    private static void Follow(SyncScenarioHarness h, ScenarioClock clock)
    {
        for (int i = 0; i <= 30; i++)
        {
            Frame(h, clock, 7.0 + i / 30.0);
            h.AdvanceMilliseconds(FrameMs);
        }
    }

    private static IReadOnlyList<double> Seeks(SyncScenarioHarness h) =>
        h.Operations.Where(o => o.Name == "seek").Select(o => o.Value ?? double.NaN).ToList();

    [Fact]
    public void Fixed30_OneFpsSuspectDuplicate_IsNotAMasterStop_TheRelocateKeepsTheLookahead()
    {
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, TimecodeFpsMode.Fixed30);
        Follow(h, clock);
        h.Operations.Clear();

        // 化けた値（Jump の保留）と、同じ値の 24fps の Duplicate 1 枚。保持値の記録（D27-d）は立つが、数える保持では
        // ないのでマスターは動いている扱い（先行量を 0 にしない）。
        // v0.6.1 D2（承認済みの期待の変更）: 以前はこの Duplicate が保留した Jump の確認になり、化けた値 8.5 + c へ relocate
        // していた（v0.5.4 の候補 1 の A の 2 回目と同じ型の誤り）。fps の疑わしい Duplicate は確認に数えないので、確定せず
        // relocate は出ない。先行量の検査（主題）は残し、本物の Jump での目標 M + c は下の別のテストで確かめる。
        Frame(h, clock, 8.5);
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 8.5, detectedFps: 24.0);

        h.SyncService.RelocateLookaheadSeconds.Should().BeApproximately(SeekCost, 1e-9,
            "fps の疑わしい Duplicate 1 枚はマスター停止ではない（先行量を 0 にしない）");
        for (int i = 0; i < 4; i++)
            h.AdvanceMilliseconds(FrameMs);
        Seeks(h).Should().BeEmpty("化けた値と fps の疑わしい Duplicate では確定せず、relocate しない（D2）");
    }

    /// <summary>
    /// v0.6.1 D2: 本物の Jump（離れた値と、疑わしくない確認の +1 フレーム）では今どおり確定し、マスターが動いている間の
    /// relocate の目標は M + c になる（先行量の検査が意味を持つ行）。
    /// </summary>
    [Fact]
    public void Fixed30_RealJump_RelocatesToTheMasterPlusTheLookahead()
    {
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, TimecodeFpsMode.Fixed30);
        Follow(h, clock);
        h.Operations.Clear();

        Frame(h, clock, 8.5);
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 8.5 + 1.0 / 30.0);

        h.SyncService.RelocateLookaheadSeconds.Should().BeApproximately(SeekCost, 1e-9, "マスターは動いている");
        for (int i = 0; i < 4; i++)
            h.AdvanceMilliseconds(FrameMs);
        Seeks(h).Should().ContainSingle("本物の Jump では relocate が 1 本出る")
            .Which.Should().BeApproximately(8.5 + 1.0 / 30.0 + SeekCost, 1e-6, "目標は M + c");
    }

    /// <summary>
    /// v0.6.1 T4（設計書 5 節）: 化けた値 8.5 と、24fps の同値の Duplicate（Fixed30）。fps の疑わしい Duplicate は Jump の
    /// 確認に数えないので、確定せず、シークは 0 本（規則 4 の入口の数え方と同じ述語）。
    /// </summary>
    [Fact]
    public void T4_Fixed30_GarbledValueWithAnFpsSuspectDuplicate_IsNotConfirmed()
    {
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, TimecodeFpsMode.Fixed30);
        Follow(h, clock);
        h.Operations.Clear();

        Frame(h, clock, 8.5);
        h.AdvanceMilliseconds(FrameMs);
        Frame(h, clock, 8.5, detectedFps: 24.0);
        for (int i = 0; i < 10; i++)
            h.AdvanceMilliseconds(FrameMs);

        Seeks(h).Should().BeEmpty("化けた値への確定も relocate も無い");
    }

    [Theory]
    [InlineData(TimecodeFpsMode.Fixed30)]
    [InlineData(TimecodeFpsMode.Auto)]
    public void RealHold_OneCountedDuplicate_IsAMasterStop_TheLookaheadIsZero(TimecodeFpsMode fpsMode)
    {
        ScenarioClock clock = NewClock();
        SyncScenarioHarness h = Arrange(clock, fpsMode);
        Follow(h, clock);
        h.SyncService.RelocateLookaheadSeconds.Should().BeApproximately(SeekCost, 1e-9, "前提: 追従中は c");

        Frame(h, clock, 8.0);   // 直前と同値の Duplicate（数える保持の 1 枚目）

        h.SyncService.RelocateLookaheadSeconds.Should().Be(0.0, "本物の保持（数える Duplicate 1 枚）では今どおり先行量 0");
    }
}
