using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

public sealed class DeferredLtcSyncTests
{
    private static void Raw(SyncScenarioHarness h, int seconds, int frame = 0, long at = 10_000) =>
        h.Controller.ReceiveFrame(new(new LtcTimecode(0, seconds / 60, seconds % 60, frame, false), 25, seconds + frame / 25d), at);

    private static (SyncScenarioHarness h, ManualTimeProvider clock) ArrangePendingLoadSync()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock) { SignalLossMode = LtcSignalLossMode.RunThrough };
        h.AddTrack("first", 0);
        Raw(h, 1);
        Raw(h, 3); // diagnostic jump
        Raw(h, 3, 1); // accepted request, while the file is still loading
        h.Operations.Clear();
        return (h, clock);
    }

    private static void Tick(SyncScenarioHarness h, ManualTimeProvider clock, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            h.Tick100Milliseconds();
        }
    }

    [Fact]
    public void HeldRequest_AfterLoadStabilityAndDebounce_SeeksExactlyOnce()
    {
        var (h, clock) = ArrangePendingLoadSync();
        Raw(h, 3, 1); // holding produces duplicates, not accepted sync frames
        h.AdvancePlayback(1.2, 2);

        Tick(h, clock, 4);

        h.Operations.Should().ContainSingle(o => o.Name == "seek" && o.Value == 3.04);
        h.AdvancePlayback(4, 20);
        Tick(h, clock, 30);
        h.Operations.Should().ContainSingle(o => o.Name == "seek", "a completed request must never pull later playback back");
        h.DisplayStates[^1].FormatText.Should().Be("NO SIGNAL");
    }

    [Fact]
    public void JumpFrame_AppliesOnce_ThenHeldDuplicatesKeepThatRequest()
    {
        // D20-b (i): Jump の直後は 1 回だけ新値で適用し、保持（Duplicate）は置き換えない。
        var (h, clock) = ArrangePendingLoadSync();
        Raw(h, 4);      // 3.04 → 4.00 は Jump。1 回だけ 4.00 を適用
        Raw(h, 4, 1);   // 4.00 → 4.04 は Normal。受理して 4.04
        Raw(h, 4, 12);  // 4.04 → 4.48 は Jump。1 回だけ 4.48 を適用
        Raw(h, 4, 12);  // 保持（Duplicate）は置き換えない
        h.AdvancePlayback(1.2, 2);

        Tick(h, clock, 4);

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle().Which.Value.Should().Be(4.48);
    }

    [Fact]
    public void HeldDuplicateAfterFileLoadRelease_ReappliesLastAcceptedTimecodeOnce()
    {
        // D20-b (i): 保持 LTC（Duplicate）でもロード解除を観測し、最後に受理した値を 1 回だけ適用する。
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.RunThrough,
        };
        h.AddTrack("first", 0);
        Raw(h, 1);
        Raw(h, 3);      // Jump（1 回だけ 3.00）
        Raw(h, 3, 1);   // Normal（3.04 を受理）
        h.AdvancePlayback(1.2, 2);   // 位置と描画フレームが進み、ロード安定条件を満たす
        h.Operations.Clear();

        Raw(h, 3, 1);   // 保持（Duplicate）。通常の同期経路は走らない

        Tick(h, clock, 3);   // ロード解除の再適用はデバウンス（250ms）経過後にシークする

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle().Which.Value.Should().Be(3.04);
    }

    [Fact]
    public void RequestAlreadyWithinTolerance_IsConsumedWithoutFuturePullback()
    {
        var (h, clock) = ArrangePendingLoadSync();
        h.AdvancePlayback(3.04, 2);
        Tick(h, clock, 4);
        h.AdvancePlayback(4.5, 20);
        Tick(h, clock, 30);
        h.Operations.Should().NotContain(o => o.Name == "seek");
    }

    [Fact]
    public void SuccessfulLoadHasNoPendingRequest_EmptyTicksDoNothing()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock) { SignalLossMode = LtcSignalLossMode.RunThrough };
        h.AddTrack("first", 0);
        Raw(h, 1);
        h.Operations.Clear();
        h.AdvancePlayback(4, 20);

        Tick(h, clock, 70);

        h.Operations.Should().NotContain(o => o.Name == "seek" || o.Name == "loadfile");
    }

    [Fact]
    public void ModeChangeDuringNewTrackLoad_ReplacesOldTimelineRequestWithSinglePosition()
    {
        var (h, clock) = ArrangePendingLoadSync();
        var next = h.AddTrack("next", 8, 10);
        Raw(h, 9);
        Raw(h, 9, 1);
        h.LoadedTrackId.Should().Be(next.Id);
        h.ChangeMode(SyncMode.Single);
        h.Operations.Clear();
        h.AdvancePlayback(1.2, 2);

        Tick(h, clock, 4);

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle().Which.Value.Should().Be(9.04);
        h.LoadedTrackId.Should().Be(next.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PausedFileWithoutProgress_TimeoutStillRetriesPendingRequest(bool single)
    {
        var (h, clock) = ArrangePendingLoadSync();
        if (single) h.ChangeMode(SyncMode.Single);
        h.ManualPause();
        h.AdvancePlayback(0, 0); // no playback/render progress, including a clip stopped at its start
        h.Operations.Clear();

        Tick(h, clock, 55);

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle().Which.Value.Should().Be(3.04);
        h.IsPaused.Should().BeTrue();
    }

    [Fact]
    public void BriefManualSeekBetweenTicks_CancelsDeferredRequest()
    {
        var (h, clock) = ArrangePendingLoadSync();
        h.BeginSeekBarInteraction();
        h.EndSeekBarInteraction(2.5);
        h.Operations.Clear();
        h.AdvancePlayback(2.5, 2);

        Tick(h, clock, 20);

        h.Operations.Should().NotContain(o => o.Name == "seek");
    }

    [Fact]
    public void SeekSettlementSuppression_RetainsOnlyNewRequestUntilItCanSeek()
    {
        var (h, clock) = ArrangePendingLoadSync();
        h.AdvancePlayback(1.2, 2);
        Tick(h, clock, 4); // first request is sent
        h.Operations.Clear();
        Raw(h, 4);
        Raw(h, 4, 1); // a new request while the previous native seek is settling
        Raw(h, 4, 1);

        Tick(h, clock, 12);

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle().Which.Value.Should().Be(4.04);
        h.AdvancePlayback(4.9, 20);
        Tick(h, clock, 30);
        h.Operations.Should().ContainSingle(o => o.Name == "seek");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewRequestWithinCurrentTolerance_WaitsForEarlierNativeSeekBeforeConsumption(bool single)
    {
        var (h, clock) = ArrangePendingLoadSync();
        if (single) h.ChangeMode(SyncMode.Single);
        h.AdvancePlayback(1.2, 2);
        Tick(h, clock, 4); // native seek to 3.04 has been issued
        h.AdvancePlayback(1, 0); // mpv still reports its old position
        h.Operations.Clear();
        Raw(h, 1); // reverse jump
        Raw(h, 1, 1); // latest accepted target 1.04 appears within tolerance now
        Raw(h, 1, 1); // subsequently held
        h.Operations.Should().NotContain(o => o.Name == "seek");

        h.AdvancePlayback(3.04, 2); // the older seek now lands at its target
        Tick(h, clock, 30);

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle().Which.Value.Should().Be(1.04);
        h.AdvancePlayback(2, 20);
        Tick(h, clock, 30);
        h.Operations.Should().ContainSingle(o => o.Name == "seek");
    }

    [Theory]
    [InlineData("sync-off")]
    [InlineData("monitor-stop")]
    [InlineData("seek")]
    [InlineData("signal-loss")]
    public void PendingRequest_DoesNotBypassControlOrSignalLossGuards(string guard)
    {
        var (h, clock) = ArrangePendingLoadSync();
        switch (guard)
        {
            case "sync-off": h.SetSyncEnabled(false); break;
            case "monitor-stop": h.Controller.MonitorStopped(null); break;
            case "seek": h.BeginSeekBarInteraction(); break;
            case "signal-loss": h.SignalLossMode = LtcSignalLossMode.Stop; break;
        }
        h.AdvancePlayback(1.2, 2);
        Tick(h, clock, 4);
        h.Operations.Should().NotContain(o => o.Name == "seek" || o.Name == "loadfile");
        if (guard == "seek")
        {
            h.EndSeekBarInteraction(2.5);
            h.Operations.Clear();
            Tick(h, clock, 10);
            h.Operations.Should().NotContain(o => o.Name == "seek");
        }
        if (guard == "signal-loss")
        {
            Raw(h, 3, 1, 10_500); // duplicate cannot recover or retry
            Tick(h, clock, 10);
            h.IsPaused.Should().BeTrue();
            h.Operations.Should().NotContain(o => o.Name == "seek" || o.Name == "signal-loss-resume");
        }
    }
}
