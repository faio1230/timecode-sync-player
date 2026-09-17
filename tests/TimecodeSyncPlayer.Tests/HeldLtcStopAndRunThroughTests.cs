using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D27: 値が進まない保持 LTC を「タイムコード停止」として扱う。
/// 停止モードは保持で一時停止し停止位置を保持値へ着地させる。ランスルーは走り続け、
/// 値が動き出したら既存の復帰（有効フレーム N 枚）で再同期する。
/// </summary>
public sealed class HeldLtcStopAndRunThroughTests
{
    private static void Raw(SyncScenarioHarness h, int seconds, int frame = 0, long at = 10_000) =>
        h.Controller.ReceiveFrame(
            new(new LtcTimecode(0, seconds / 60, seconds % 60, frame, false), 25, seconds + frame / 25d), at);

    private static void Tick(SyncScenarioHarness h, ManualTimeProvider clock, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            h.Tick100Milliseconds();
        }
    }

    /// <summary>
    /// 1.04 まで進み、2.04 へ Jump して適用、その後 2.04 の保持。停止までに 2.5 まで進む。
    /// 最後の有効フレームは 10_040、最後の保持フレームは 10_200。
    /// </summary>
    private static (SyncScenarioHarness h, ManualTimeProvider clock) ArrangeHeldAt204(LtcSignalLossMode mode)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true) { SignalLossMode = mode };
        h.AddTrack("first", 0);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        Raw(h, 1, 0, 10_000);
        Raw(h, 1, 1, 10_040);
        Raw(h, 2, 1, 10_080);
        h.AdvancePlayback(2.04, 5);
        Raw(h, 2, 1, 10_120);
        Raw(h, 2, 1, 10_200);
        h.AdvancePlayback(2.5, 5);
        h.Operations.Clear();
        return (h, clock);
    }

    [Fact]
    public void StopMode_HeldLtc_PausesAndLandsOnHeldValue()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = ArrangeHeldAt204(LtcSignalLossMode.Stop);

        Tick(h, clock, 3);

        h.IsPaused.Should().BeTrue();
        h.Operations.Should().Contain(o => o.Name == "signal-loss-pause");
        h.Operations.Where(o => o.Name == "seek")
            .Should().ContainSingle("保持で停止したときに保持値へ 1 回だけ着地する")
            .Which.Value.Should().BeApproximately(2.04, 0.02);
        h.PlaybackSeconds.Should().BeApproximately(2.04, 0.02);
        h.DisplayStates[^1].PauseReason.Should().Be("タイムコード停止で停止中");
    }

    [Fact]
    public void StopMode_SilentLoss_PausesWithoutLanding()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true) { SignalLossMode = LtcSignalLossMode.Stop };
        h.AddTrack("first", 0);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        Raw(h, 1, 0, 10_000);
        Raw(h, 1, 1, 10_040);
        h.AdvancePlayback(1.3, 5);
        h.Operations.Clear();

        Tick(h, clock, 3);

        h.IsPaused.Should().BeTrue();
        h.Operations.Should().NotContain(o => o.Name == "seek", "無音では着地先の保持値が無い");
        h.DisplayStates[^1].PauseReason.Should().Be("信号断で停止中");
    }

    [Fact]
    public void StopMode_HeldThenAdvancing_ResumesAndClearsReason()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = ArrangeHeldAt204(LtcSignalLossMode.Stop);
        Tick(h, clock, 3);
        h.IsPaused.Should().BeTrue();
        h.Operations.Clear();

        Raw(h, 2, 2, 10_300);
        Raw(h, 2, 3, 10_340);
        Raw(h, 2, 4, 10_420);

        h.Operations.Should().Contain(o => o.Name == "signal-loss-resume");
        h.IsPaused.Should().BeFalse();
        h.DisplayStates[^1].PauseReason.Should().BeEmpty();
    }

    [Fact]
    public void StopMode_HeldThenJump_ResumesAndResyncs()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = ArrangeHeldAt204(LtcSignalLossMode.Stop);
        Tick(h, clock, 3);
        h.IsPaused.Should().BeTrue();
        h.Operations.Clear();

        Raw(h, 3, 0, 10_300);
        Raw(h, 3, 1, 10_340);
        Raw(h, 3, 2, 10_380);
        for (int i = 0; i < 8; i++)
        {
            Raw(h, 3, 3 + i * 2, 10_420 + i * 100);
            Tick(h, clock, 1);
        }

        h.IsPaused.Should().BeFalse();
        h.Operations.Should().Contain(o => o.Name == "signal-loss-resume");
        h.PlaybackSeconds.Should().BeGreaterThan(3.0, "復帰後は新しい LTC へ再同期する");
    }

    [Fact]
    public void RunThroughMode_HeldLtc_KeepsPlayingWithoutPause()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = ArrangeHeldAt204(LtcSignalLossMode.RunThrough);

        Tick(h, clock, 3);

        h.IsPaused.Should().BeFalse();
        h.Operations.Should().NotContain(o => o.Name == "signal-loss-pause");
        h.Operations.Should().NotContain(o => o.Name == "signal-loss-resume");
        h.DisplayStates[^1].PauseReason.Should().BeEmpty();
    }

    [Fact]
    public void RunThroughMode_HeldThenAdvancing_ResyncsByJump()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = ArrangeHeldAt204(LtcSignalLossMode.RunThrough);
        Tick(h, clock, 3);
        h.Operations.Clear();

        Raw(h, 2, 2, 10_300);
        Raw(h, 2, 3, 10_340);
        Raw(h, 2, 4, 10_420);
        Tick(h, clock, 8);

        h.IsPaused.Should().BeFalse();
        h.PlaybackSeconds.Should().BeApproximately(2.16, 0.05, "保持が明けたら LTC 側へ戻る");
    }

    [Fact]
    public void StopMode_HeldThenJumpToNewHold_RecoversImmediatelyAndPausesAtNewValue()
    {
        // S-2: 保持 8.0 で一時停止 → 次の保持値 20.0 への Jump が 1 枚でも届けば復帰し、
        // そのまま保持が続けば新しい値で改めて一時停止する。
        (SyncScenarioHarness h, ManualTimeProvider clock) = ArrangeHeldAt204(LtcSignalLossMode.Stop);
        Tick(h, clock, 3);
        h.IsPaused.Should().BeTrue();
        h.Operations.Clear();

        Raw(h, 3, 0, 10_300);

        h.IsPaused.Should().BeFalse("保持損失中の Jump 1 枚で復帰する");
        h.Operations.Should().Contain(o => o.Name == "signal-loss-resume");

        for (int i = 1; i <= 8; i++)
        {
            Raw(h, 3, 0, 10_300 + i * 100);
            Tick(h, clock, 1);
        }

        h.IsPaused.Should().BeTrue("新しい値の保持が続けば損失で一時停止する");
        h.PlaybackSeconds.Should().BeApproximately(3.0, 0.05);
        h.DisplayStates[^1].PauseReason.Should().Be("タイムコード停止で停止中");
    }

    [Fact]
    public void StopMode_HeldThenJumpToNewHold_RecoversImmediately_EvenAfterReasonDowngraded()
    {
        // D27-c: フレーム処理の遅延で理由が信号断へ下がっていても、直近に保持フレームが
        // 届いていれば Jump 1 枚で復帰し、そのまま保持が続けば新しい値で再損失する。
        (SyncScenarioHarness h, ManualTimeProvider clock) = ArrangeHeldAt204(LtcSignalLossMode.Stop);
        Tick(h, clock, 3);
        h.IsPaused.Should().BeTrue();
        h.Operations.Clear();

        // 保持のまま Tick だけが進み、理由が信号断へ下がる。
        Tick(h, clock, 3);
        // 保持フレームは届き続けている。
        Raw(h, 2, 1, 10_700);

        // Jump 1 枚で復帰（restored と同時に新しい値へ適用）。
        Raw(h, 3, 0, 10_800);

        h.IsPaused.Should().BeFalse(
            $"保持損失の直後の Jump 1 枚で復帰する ops=[{string.Join(",", h.Operations.Select(o => o.Name))}] reason={h.DisplayStates[^1].PauseReason}");
        h.Operations.Should().Contain(o => o.Name == "signal-loss-resume");

        // その後の Duplicate が続けば新しい値で再損失する。
        for (int i = 1; i <= 12; i++)
        {
            Raw(h, 3, 0, 10_800 + i * 100);
            Tick(h, clock, 1);
        }

        h.IsPaused.Should().BeTrue("新しい値の保持が続けば損失で一時停止する");
        h.PlaybackSeconds.Should().BeApproximately(3.0, 0.05);
        h.DisplayStates[^1].PauseReason.Should().Be("タイムコード停止で停止中");
    }

    [Fact]
    public void RunThroughMode_ManualLoadRelease_AfterDeferredRetryStillReappliesHeldValueOnce()
    {
        // S-4: 手動ロードの解除を Tick 側の保留シーク再送が先に回収しても（尺未確定で
        // None → Complete）、保持値の 1 回適用が必ず走り、clamp 位置へ着地する。
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true) { SignalLossMode = LtcSignalLossMode.RunThrough };
        h.AddTrack("first", 0);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        Raw(h, 1, 0, 10_000);
        Raw(h, 1, 1, 10_040);
        h.AdvancePlayback(1.04, 5);

        h.BeginManualFileLoad();
        h.NativeSeeking = true;
        Raw(h, 8, 0, 10_080);
        h.Operations.Clear();

        // ロードが進み、保留再送が解除を先に回収する（尺は未確定なので決定は None）。
        h.NativeSeeking = false;
        h.AdvancePlayback(0.2, 3);
        h.SetDurationSeconds(0);
        Tick(h, clock, 3);
        h.Operations.Should().NotContain(o => o.Name == "seek", "尺が未確定のうちは着地しない");
        h.IsPaused.Should().BeFalse();

        // 尺が確定して保持フレームが届いたら、解除の 1 回適用が clamp 位置へ着地する
        // （解除でデバウンスが再スタートするため 1 回は Deferred、次の Tick で再送される）。
        h.SetDurationSeconds(5);
        Raw(h, 8, 0, 10_400);
        Tick(h, clock, 1);

        h.Operations.Where(o => o.Name == "seek")
            .Should().ContainSingle().Which.Value.Should().BeApproximately(5.0, 0.05);
        h.PlaybackSeconds.Should().BeApproximately(5.0, 0.05);

        Raw(h, 8, 0, 10_440);
        Tick(h, clock, 2);
        h.Operations.Where(o => o.Name == "seek")
            .Should().ContainSingle("解除の 1 回適用は 1 回だけ");
    }
}
