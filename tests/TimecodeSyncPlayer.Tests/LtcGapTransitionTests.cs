using FluentAssertions;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

public sealed class LtcGapTransitionTests
{
    private static void Raw(SyncScenarioHarness h, int seconds, int frame = 0, long at = 10_000) =>
        h.Controller.ReceiveFrame(new(new LtcTimecode(0, 0, seconds, frame, false), 25, seconds + frame / 25d), at);

    [Theory]
    [InlineData(GapBehavior.Black, false)]
    [InlineData(GapBehavior.Freeze, false)]
    [InlineData(GapBehavior.Freeze, true)]
    public void FirstAcceptedFrameAfterGap_LoadsNextTrackWithoutSeekingOldTrack(GapBehavior behavior, bool completed)
    {
        var h = new SyncScenarioHarness { GapBehavior = behavior };
        h.AddTrack("first", 0);
        var next = h.AddTrack("next", 8);
        h.ManualPlay();
        Raw(h, 1);
        Raw(h, 7, 23); // A jump is diagnostic-only; the following stable frame enters the gap.
        Raw(h, 7, 24);
        if (completed) h.CompleteFreezeCapture();
        h.Operations.Clear();

        Raw(h, 8);

        h.LoadedTrackId.Should().Be(next.Id);
        h.GapState.Should().Be(GapState.Inactive);
        h.Operations.Should().ContainSingle(o => o.Name == "loadfile" && o.Value == 0);
        h.Operations.Should().NotContain(o => o.Name == "seek");
        h.IsPaused.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlackChoice_CancelsPendingFreezeOnNextAcceptedFrame(bool waiting)
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("first", 0);
        Raw(h, 6);
        h.ArrangeGapStateForModel(waiting ? GapState.WaitingForFrameStep : GapState.EnteringFreeze);
        h.GapBehavior = GapBehavior.Black;

        Raw(h, 6, 1);

        h.GapState.Should().Be(GapState.BlackFrameActive);
        h.RenderSurface.Should().Be(ScenarioRenderSurface.Black);
    }

    [Fact]
    public void GapChoice_ReevaluatesImmediatelyWithoutAnotherFrame()
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("first", 0);
        h.ManualPlay();
        Raw(h, 6);

        h.GapBehavior = GapBehavior.Black;

        h.GapState.Should().Be(GapState.BlackFrameActive);
        h.RenderSurface.Should().Be(ScenarioRenderSurface.Black);
        h.Controller.Tick(10_250);
        h.DisplayStates[^1].FormatText.Should().Be("NO SIGNAL");
        h.GapBehavior = GapBehavior.Freeze;
        h.GapState.Should().Be(GapState.EnteringFreeze);
        h.SetSyncEnabled(false);
        h.IsPaused.Should().BeFalse("gap pause ownership survives a change of gap behavior");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GapExitFailure_PreservesPauseOwnershipForRetry(bool sameTrack)
    {
        var h = new SyncScenarioHarness { GapBehavior = GapBehavior.Black };
        h.AddTrack("first", 0);
        h.AddTrack("next", 8);
        h.ManualPlay();
        Raw(h, 4, 24);
        Raw(h, 5);
        h.LoadSucceeds = false;
        h.SeekSucceeds = false;
        int target = sameTrack ? 4 : 8;
        Raw(h, target);
        Raw(h, target, 1);
        h.GapState.Should().Be(GapState.BlackFrameActive);
        h.IsPaused.Should().BeTrue();

        h.LoadSucceeds = true;
        h.SeekSucceeds = true;
        Raw(h, target, 2);

        h.GapState.Should().Be(GapState.Inactive);
        h.IsPaused.Should().BeFalse();
    }

    [Fact]
    public void EnableSync_UsesTheJumpFrameAcceptedOnce()
    {
        // D20-b (i): Jump は 1 回だけ新値で受理される。同期 ON の再適用もその受理値を使う
        // （旧挙動の「診断 Jump を無視して直前の受理値へ戻す」から変更）。
        // D30: 別トラックへの Jump は次の 1 フレームの連続を確認してから受理する。
        var h = new SyncScenarioHarness();
        h.AddTrack("first", 0);
        var next = h.AddTrack("next", 8);
        h.SetSyncEnabled(false);
        Raw(h, 1);
        Raw(h, 9);   // Jump（D30: 未確認のため保留）
        Raw(h, 9);   // 同値の Duplicate で確認 → 9.00 を受理
        h.Controller.LastLtcSeconds.Should().Be(9);
        h.Operations.Clear();

        h.SetSyncEnabled(true);

        h.LoadedTrackId.Should().Be(next.Id);
        h.Operations.Should().ContainSingle(o => o.Name == "loadfile" && o.Value == 1);
    }

    [Fact]
    public void ContinueChoice_ReevaluatesAcceptedSingleModePositionImmediately()
    {
        var h = new SyncScenarioHarness { GapBehavior = GapBehavior.Black };
        h.AddTrack("first", 0);
        h.ChangeMode(SyncMode.Single);
        Raw(h, 6);

        h.ChangeMode(SyncMode.Continue);

        h.GapState.Should().Be(GapState.BlackFrameActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManualGapExit_ResumesOnlyGapOwnedPause(bool userPaused)
    {
        var h = new SyncScenarioHarness { GapBehavior = GapBehavior.Black };
        h.AddTrack("first", 0);
        if (userPaused) h.ManualPause(); else h.ManualPlay();
        Raw(h, 6);

        h.SetSyncEnabled(false);

        h.IsPaused.Should().Be(userPaused);
    }

    [Fact]
    public void Reevaluate_DoesNotInventSignalFreshnessOrRecoveryFrames()
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("first", 0);
        h.ManualPlay();
        Raw(h, 1);
        h.Controller.Tick(10_250);
        h.Operations.Clear();
        h.SetSyncEnabled(false);
        h.SetSyncEnabled(true);
        h.ChangeMode(SyncMode.Single);
        h.ChangeMode(SyncMode.Continue);
        h.IsPaused.Should().BeTrue();
        h.Operations.Should().NotContain(o => o.Name == "loadfile" || o.Name == "seek" || o.Name == "signal-loss-resume");
        Raw(h, 1, 1, 10_260);
        Raw(h, 1, 2, 10_270);
        h.IsPaused.Should().BeTrue();
        Raw(h, 1, 3, 10_280);
        h.IsPaused.Should().BeFalse();
    }

    [Theory]
    [InlineData("no-frame")]
    [InlineData("stopped")]
    [InlineData("seeking")]
    public void Reevaluate_RespectsUnavailableInputAndManualSeek(string guard)
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("first", 0);
        h.SetSyncEnabled(false);
        if (guard != "no-frame") Raw(h, 1);
        if (guard == "stopped") h.Controller.MonitorStopped(null);
        if (guard == "seeking") h.BeginSeekBarInteraction();
        h.Operations.Clear();

        h.SetSyncEnabled(true);

        h.Operations.Should().NotContain(o => o.Name == "loadfile" || o.Name == "seek");
    }
}

