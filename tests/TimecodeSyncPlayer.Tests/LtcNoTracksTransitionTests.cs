using FluentAssertions;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

public sealed class LtcNoTracksTransitionTests
{
    private static void Raw(SyncScenarioHarness h, int frame, long receivedAt = 10_000) =>
        h.Controller.ReceiveFrame(new(new LtcTimecode(0, 0, 1, frame, false), 25, 1 + frame / 25d), receivedAt);

    private static SyncScenarioHarness EmptyPlaylistWithLoadedClip(bool userPaused = false)
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("last-loaded", 0);
        Raw(h, 0);
        if (userPaused) h.ManualPause(); else h.ManualPlay();
        h.Playlist.Tracks.Clear();
        return h;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NoTracks_HeldLtcFreezeToBlackCancelsCaptureImmediately(int capturePhase)
    {
        var h = EmptyPlaylistWithLoadedClip();
        Raw(h, 1, 10_040);
        if (capturePhase == 1) h.ArrangeGapStateForModel(GapState.WaitingForFrameStep);
        if (capturePhase == 2) h.CompleteFreezeCapture();
        Raw(h, 1, 10_200); // A held duplicate is not a fresh accepted frame.
        h.Operations.Clear();

        h.GapBehavior = GapBehavior.Black;

        h.GapState.Should().Be(GapState.ForceBlack);
        h.RenderSurface.Should().Be(ScenarioRenderSurface.Black);
        h.Operations.Should().Contain(o => o.Name == "clear-freeze");
        h.Operations.Should().NotContain(o => o.Name == "seek" || o.Name == "loadfile");
        h.Controller.Tick(10_290);
        h.DisplayStates[^1].FormatText.Should().Be("NO SIGNAL", "neither the duplicate nor the setting change refreshes the signal");
        h.SetSyncEnabled(false);
        h.IsPaused.Should().BeFalse("the original gap-owned pause survives the switch");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoTracks_HeldLtcBlackToFreezeCapturesLastClipAndPreservesPauseOwner(bool userPaused)
    {
        var h = EmptyPlaylistWithLoadedClip(userPaused);
        h.GapBehavior = GapBehavior.Black;
        Raw(h, 1);
        h.GapState.Should().Be(GapState.ForceBlack);
        h.Operations.Clear();

        h.GapBehavior = GapBehavior.Freeze;

        h.GapState.Should().Be(GapState.EnteringFreeze);
        h.Operations.Should().ContainSingle(o => o.Name == "seek" && o.Value == 4.96);
        h.IsPaused.Should().BeTrue();
        h.GapBehavior = GapBehavior.Black;
        h.GapBehavior = GapBehavior.Freeze;
        h.SetSyncEnabled(false);
        h.IsPaused.Should().Be(userPaused);
    }

    [Fact]
    public void NoTracks_WithNoLoadedClip_RemainsBlackWithoutTryingToCapture()
    {
        var h = new SyncScenarioHarness { GapBehavior = GapBehavior.Black };
        Raw(h, 0);
        h.Operations.Clear();

        h.GapBehavior = GapBehavior.Freeze;
        h.GapBehavior = GapBehavior.Black;
        h.GapBehavior = GapBehavior.Freeze;

        h.GapState.Should().Be(GapState.ForceBlack);
        h.RenderSurface.Should().Be(ScenarioRenderSurface.Black);
        h.Operations.Should().NotContain(o => o.Name == "seek" || o.Name == "loadfile");
    }

    [Fact]
    public void NoTracks_SettingChangesDoNotReleaseSignalLossPauseOrCountRecoveryFrames()
    {
        var h = EmptyPlaylistWithLoadedClip();
        h.Controller.Tick(10_250);
        h.IsPaused.Should().BeTrue();
        h.Operations.Clear();

        h.GapBehavior = GapBehavior.Black;
        h.GapBehavior = GapBehavior.Freeze;
        h.SetSyncEnabled(false);
        h.SetSyncEnabled(true);
        h.ChangeMode(SyncMode.Single);
        h.ChangeMode(SyncMode.Continue);

        h.GapState.Should().Be(GapState.Inactive);
        h.IsPaused.Should().BeTrue();
        h.Operations.Should().NotContain(o => o.Name == "seek" || o.Name == "signal-loss-resume" || o.Name == "pause-for-gap");
        Raw(h, 1, 10_260);
        Raw(h, 2, 10_270);
        h.Operations.Should().NotContain(o => o.Name == "signal-loss-resume");
        Raw(h, 3, 10_280);
        h.Operations.Should().ContainSingle(o => o.Name == "signal-loss-resume");
        h.GapState.Should().Be(GapState.EnteringFreeze);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoTracks_BlackCancellationClearsPendingCaptureAndPreservesPauseOwnership(bool waiting)
    {
        var gap = new GapFreezeHandler();
        var trackId = Guid.NewGuid();
        gap.RecordPauseOwnership(wasPlaybackPaused: false);
        gap.EnterFreezeCaptureWithReload(trackId, 4.96, "C:/last-loaded.mp4");
        gap.CachedTrackId = trackId;
        gap.CachedTargetSeconds = 4.96;
        if (waiting) gap.CurrentState = GapState.WaitingForFrameStep;
        bool captured = false;
        var pending = new DeferredGapStateOperation(gap.CurrentState, () => gap.CurrentState, () => captured = true);

        var action = gap.DecideNoTracksEnter(GapBehavior.Black, trackId);
        pending.RunIfCurrent();

        action.Type.Should().Be(GapEnterActionType.ForceBlack);
        captured.Should().BeFalse();
        gap.PendingPath.Should().BeNull();
        gap.PendingTrackId.Should().BeNull();
        gap.PendingTargetSeconds.Should().Be(0);
        gap.StartedAt.Should().Be(DateTime.MinValue);
        gap.LastReloadAt.Should().Be(DateTime.MinValue);
        gap.CachedTrackId.Should().BeNull();
        gap.CachedTargetSeconds.Should().Be(0);
        gap.PeekGapExit().ShouldResumePlayback.Should().BeTrue();
    }
}
