using System.Diagnostics;
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
    public void StopMode_HeldDuplicateAfterLastAccepted_LandsOnTheHeldValue()
    {
        // D27-d: 着地目標は「保持として届いている値（Duplicate）」にする。直前の受理値では
        // 保持値に 1 フレーム届かない（R-1 で 1 フレーム手前に停止）。
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true) { SignalLossMode = LtcSignalLossMode.Stop };
        h.AddTrack("first", 0);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();

        // 受理済みの値は 1.04 まで。保持値 1.08 は Duplicate として届いている。
        h.Controller.ReceiveProcessedFrame(Processed(1.00, TimecodeFrameDiagnosticStatus.Normal), 10_000);
        h.Controller.ReceiveProcessedFrame(Processed(1.04, TimecodeFrameDiagnosticStatus.Normal), 10_040);
        h.Controller.ReceiveProcessedFrame(Processed(1.08, TimecodeFrameDiagnosticStatus.Duplicate), 10_120);
        h.Controller.ReceiveProcessedFrame(Processed(1.08, TimecodeFrameDiagnosticStatus.Duplicate), 10_200);
        h.Operations.Clear();

        Tick(h, clock, 3);

        h.IsPaused.Should().BeTrue();
        h.Operations.Where(o => o.Name == "seek")
            .Should().ContainSingle("保持で停止したときに保持値へ 1 回だけ着地する")
            .Which.Value.Should().BeApproximately(1.08, 0.001,
                "着地目標は保持として届いた値（1.08）で、直前の受理値（1.04）ではない");
        h.DisplayStates[^1].PauseReason.Should().Be("タイムコード停止で停止中");
    }

    private static LtcFrameProcessingResult Processed(double seconds, TimecodeFrameDiagnosticStatus status) =>
        new("scenario", $"{seconds:F3} s", seconds, 25, "fps: 25",
            new TimecodeFrameDiagnosticResult(status, 0, 0),
            ShouldApplySync: status is TimecodeFrameDiagnosticStatus.Normal or TimecodeFrameDiagnosticStatus.Initial,
            ShouldLogFps: false);

    [Fact]
    public void StopMode_HeldDuplicate_LandingTargetDoesNotAddSampleClockAge()
    {
        // D27-d: 保持値は凍結されて進まないため、着地目標にサンプル時計の age（処理遅延）を
        // 足さない。届いた値そのもの（+T3 オフセット）へ着地する。
        long qpc = 5_000_000;
        long frameEnd = qpc - 30 * (Stopwatch.Frequency / 1000); // 30ms 前に終わったフレーム
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true, sampleClockEnabled: true, getQpc: () => qpc)
        {
            SignalLossMode = LtcSignalLossMode.Stop,
        };
        h.AddTrack("first", 0);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();

        RawFrame(h, 1, 0, 10_000, frameEnd);
        RawFrame(h, 1, 1, 10_040, frameEnd);
        RawFrame(h, 1, 1, 10_200, frameEnd); // Duplicate（保持値 1.04）
        h.AdvancePlayback(1.2);              // D35: 1 フレーム超の行き過ぎで明示着地の対象にする
        h.Operations.Clear();

        Tick(h, clock, 3);

        h.IsPaused.Should().BeTrue();
        h.Operations.Where(o => o.Name == "seek")
            .Should().ContainSingle()
            .Which.Value.Should().BeApproximately(1.04, 0.001,
                "保持値は凍結されているため age（30ms）を足さない");
    }

    private static void RawFrame(SyncScenarioHarness h, int seconds, int frame, long at, long frameEndTimestamp)
    {
        var timecode = new LtcTimecode(0, seconds / 60, seconds % 60, frame, false);
        h.Controller.ReceiveFrame(
            new LtcFrameReceivedEventArgs(timecode, 25, seconds + frame / 25d, frameEndTimestamp, 0), at);
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
        // 0.4.6: フレームは実機と同じく 40ms ごとに届き、そのぶん時計も進める（1 秒ぶん）。
        // 以前はフレームを受けた直後に時計を進めずに次を評価していた（dt = 0 で 80ms 進む）ため、
        // ゲートが毎回「ありえない跳ね」として弾き、弾いても残っていた連続回数（欠陥。0.4.6 で修正）が
        // 積み上がってシークに至っていた。試験はその欠陥のおかげで通っていた。
        // 1 秒ぶん送るのは、直前の保持着地（2.04）の直後 500ms はその目標の近くでシークを抑止する
        // 既存の仕組み（D20）があり、このハーネスは再生位置を自動で進めないので抑止が 500ms 続くため。
        for (int i = 0; i < 25; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(40));
            Raw(h, 3, 3 + i, 10_420 + i * 40);
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
        // D37-b: 不足が 1 秒未満なのでシークせず速度補正に任せる（ハーネスはレートで位置を動かさない）。
        h.Operations.Should().NotContain(o => o.Name == "seek");
        h.AppliedRates.Should().NotBeEmpty();
        h.AppliedRates[^1].Should().BeApproximately(0.9, 1e-9, "行き過ぎを緩めて LTC 側へ寄せる");
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
        // （解除でデバウンスが再スタートし、D37-a のゲートも窓が埋まるまで保留を維持するため、
        //  Tick の再送で 3 サンプルそろってから着地する）。
        h.SetDurationSeconds(5);
        Raw(h, 8, 0, 10_400);
        Tick(h, clock, 3);

        h.Operations.Where(o => o.Name == "seek")
            .Should().ContainSingle().Which.Value.Should().BeApproximately(5.0, 0.05);
        h.PlaybackSeconds.Should().BeApproximately(5.0, 0.05);

        Raw(h, 8, 0, 10_440);
        Tick(h, clock, 2);
        h.Operations.Where(o => o.Name == "seek")
            .Should().ContainSingle("解除の 1 回適用は 1 回だけ");
    }
}
