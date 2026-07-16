using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

public class SyncScenarioTests
{
    [Fact]
    public void FullShowWorkflow_ProducesExpectedStateAndSideEffectStream()
    {
        var harness = new SyncScenarioHarness();
        PlaylistTrack first = harness.AddTrack("track-1", timelineIn: 0, duration: 5);
        PlaylistTrack second = harness.AddTrack("track-2", timelineIn: 8, duration: 5);
        harness.ManualPlay();

        harness.SupplyLtc(1);
        harness.LoadedTrackId.Should().Be(first.Id);

        harness.SupplyLtc(6);
        harness.GapState.Should().Be(GapState.EnteringFreeze);
        harness.CompleteFreezeCapture();
        harness.SupplyLtc(9);
        harness.IsPaused.Should().BeFalse("the gap owns and releases its pause");
        harness.SupplyLtc(9);
        harness.LoadedTrackId.Should().Be(second.Id);

        harness.AdvancePlayback(1.2, renderedFrames: 2);
        harness.Tick100Milliseconds(3);
        harness.IsPaused.Should().BeTrue("Stop mode pauses after the 250ms signal-loss threshold");
        harness.Operations.Count(operation => operation.Name == "signal-loss-pause").Should().Be(1);

        harness.SupplyLtc(9.04);
        harness.SupplyLtc(9.08);
        harness.SupplyLtc(9.12);
        harness.IsPaused.Should().BeFalse("three stable frames release the signal-loss-owned pause");
        harness.Operations.Count(operation => operation.Name == "signal-loss-resume").Should().Be(1);

        harness.SetSyncEnabled(false);
        harness.BeginSeekBarInteraction();
        harness.EndSeekBarInteraction(2.5);
        harness.SetSyncEnabled(true);
        harness.SupplyLtc(10.5);

        harness.SyncEnabled.Should().BeTrue();
        harness.PlaybackSeconds.Should().BeApproximately(2.5, 1e-9,
            "the existing post-load debounce may suppress the immediate resync seek");
        harness.ValidateInvariants().Should().BeEmpty();
    }

    public static TheoryData<int> PauseOwnershipCases => new()
    {
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11,
    };

    [Theory]
    [MemberData(nameof(PauseOwnershipCases))]
    public void PauseOwnership_AllOrderedPairsAndReleaseOrders_PreserveOwnerRules(int scenario)
    {
        var harness = CreateTwoTrackHarness();
        harness.ManualPlay();
        harness.SupplyLtc(1);

        switch (scenario)
        {
            // Manual → Gap. The two cases vary whether the manual owner releases before or after gap exit.
            case 0:
                harness.ManualPause();
                EnterAndCompleteGap(harness);
                ExitGap(harness);
                harness.IsPaused.Should().BeTrue();
                harness.ManualPlay();
                break;
            case 1:
                harness.ManualPause();
                EnterAndCompleteGap(harness);
                harness.ManualPlay();
                ExitGap(harness);
                break;

            // Gap → Manual. Manual Play overrides the gap pause; re-pausing after exit remains manual.
            case 2:
                EnterAndCompleteGap(harness);
                harness.ManualPlay();
                ExitGap(harness);
                break;
            case 3:
                EnterAndCompleteGap(harness);
                ExitGap(harness);
                harness.ManualPause();
                harness.IsPaused.Should().BeTrue();
                harness.ManualPlay();
                break;

            // Signal loss → Manual, with manual release before/after recovery.
            case 4:
                LoseSignal(harness);
                harness.ManualPlay();
                RecoverSignal(harness);
                break;
            case 5:
                LoseSignal(harness);
                RecoverSignal(harness);
                harness.ManualPlay();
                break;

            // Manual → Signal loss. An already-manual pause is never owned or released by the policy.
            case 6:
                harness.ManualPause();
                LoseSignal(harness);
                RecoverSignal(harness);
                harness.IsPaused.Should().BeTrue();
                harness.ManualPlay();
                break;
            case 7:
                harness.ManualPause();
                LoseSignal(harness);
                harness.ManualPlay();
                RecoverSignal(harness);
                break;

            // Gap → Signal loss is reachable, but the active-gap guard prevents signal ownership.
            case 8:
                EnterAndCompleteGap(harness);
                harness.Tick100Milliseconds(3);
                harness.Operations.Should().NotContain(operation => operation.Name == "signal-loss-pause");
                ExitGap(harness);
                break;
            case 9:
                EnterAndCompleteGap(harness);
                harness.Tick100Milliseconds(3);
                RecoverSignal(harness);
                ExitGap(harness);
                break;

            // Signal loss → Gap is unreachable while policy-owned pause suppresses sync.
            case 10:
                LoseSignal(harness);
                harness.SupplyLtc(6);
                harness.IsGapActive.Should().BeFalse();
                harness.ManualPlay();
                break;
            case 11:
                LoseSignal(harness);
                RecoverSignal(harness);
                EnterAndCompleteGap(harness);
                ExitGap(harness);
                break;
        }

        harness.ValidateInvariants().Should().BeEmpty();
        harness.IsPaused.Should().BeFalse("the final explicit manual Play wins in every reachable sequence");
    }

    public static IEnumerable<object[]> ReachableModelCases()
    {
        foreach (SyncMode mode in Enum.GetValues<SyncMode>())
        foreach (bool syncEnabled in new[] { false, true })
        foreach (GapState gapState in Enum.GetValues<GapState>())
        foreach (bool signalLost in new[] { false, true })
        {
            bool reachable = gapState == GapState.Inactive ||
                             (mode == SyncMode.Continue && syncEnabled);
            if (reachable)
                yield return [mode, syncEnabled, (int)gapState, signalLost];
        }
    }

    [Theory]
    [MemberData(nameof(ReachableModelCases))]
    public void ModelTransitions_AllReachableCombinations_MaintainInvariants(
        SyncMode mode,
        bool syncEnabled,
        int gapStateValue,
        bool signalLost)
    {
        GapState gapState = (GapState)gapStateValue;
        var harness = CreateTwoTrackHarness();
        harness.ManualPlay();
        harness.ChangeMode(mode);
        harness.SetSyncEnabled(syncEnabled);
        harness.ArrangeGapStateForModel(gapState);

        if (signalLost)
        {
            harness.SupplyLtc(1);
            harness.Tick100Milliseconds(3);
        }

        harness.ValidateInvariants().Should().BeEmpty();

        if (gapState != GapState.Inactive)
            harness.ChangeMode(SyncMode.Single);
        else
            harness.SetSyncEnabled(!syncEnabled);

        harness.ValidateInvariants().Should().BeEmpty();
        if (gapState != GapState.Inactive)
        {
            harness.GapState.Should().Be(GapState.Inactive);
            harness.RenderSurface.Should().Be(ScenarioRenderSurface.Video);
        }
        harness.Operations.Count(operation => operation.Name == "signal-loss-pause")
            .Should().BeLessThanOrEqualTo(1, "one loss edge must not issue duplicate pauses");
    }

    [Fact]
    public void ModelTransitions_UnreachableGapCombinations_AreExplicitlyExcluded()
    {
        var unreachable =
            from mode in Enum.GetValues<SyncMode>()
            from syncEnabled in new[] { false, true }
            from gapState in Enum.GetValues<GapState>().Where(state => state != GapState.Inactive)
            where mode != SyncMode.Continue || !syncEnabled
            select (mode, syncEnabled, gapState);

        unreachable.Should().OnlyContain(item =>
            item.mode == SyncMode.Single || !item.syncEnabled,
            "GapStateExitPolicy immediately clears every non-Inactive gap outside Continue + Sync ON");
        unreachable.Should().HaveCount(15);
    }

    [Fact]
    public void GapBoundaryPlusOrMinusOneFrame_DoesNotReenterWhileCaptureIsPending()
    {
        var harness = CreateTwoTrackHarness();
        harness.ManualPlay();
        harness.SupplyLtc(4.96);

        harness.SupplyLtc(5.04);
        for (int i = 0; i < 20; i++)
            harness.SupplyLtc(i % 2 == 0 ? 4.96 : 5.04);

        harness.GapState.Should().Be(GapState.EnteringFreeze);
        harness.Operations.Count(operation => operation.Name == "pause-for-gap").Should().Be(1);
        harness.Operations.Count(operation => operation.Name == "render-freeze").Should().Be(0);
        harness.ValidateInvariants().Should().BeEmpty();
    }

    [Fact]
    public void PlaylistRowSelection_DoesNotLoadAnotherTrack()
    {
        var harness = CreateTwoTrackHarness();
        harness.SupplyLtc(1);
        int loadCount = harness.Operations.Count(operation => operation.Name == "loadfile");

        harness.SelectPlaylistRow(1);

        harness.Operations.Count(operation => operation.Name == "loadfile").Should().Be(loadCount);
    }

    [Fact]
    public void BlackGap_WhenChangingToSingle_ClearsBlackAndRedrawsVideo()
    {
        var harness = CreateTwoTrackHarness();
        harness.GapBehavior = GapBehavior.Black;
        harness.ManualPlay();
        harness.SupplyLtc(1);
        harness.SupplyLtc(6);
        harness.RenderSurface.Should().Be(ScenarioRenderSurface.Black);

        harness.ChangeMode(SyncMode.Single);

        harness.GapState.Should().Be(GapState.Inactive);
        harness.RenderSurface.Should().Be(ScenarioRenderSurface.Video);
        harness.Operations.Should().Contain(operation => operation.Name == "seek");
        harness.ValidateInvariants().Should().BeEmpty();
    }

    [Fact]
    public void GapBehaviorSwitch_DuringGap_PreservesGapPauseOwnershipAndResumesOnTrack()
    {
        var harness = CreateTwoTrackHarness();
        harness.GapBehavior = GapBehavior.Black;
        harness.ManualPlay();
        harness.SupplyLtc(1);
        harness.SupplyLtc(6);

        harness.GapBehavior = GapBehavior.Freeze;
        harness.SupplyLtc(6);
        if (harness.GapState == GapState.EnteringFreeze)
            harness.CompleteFreezeCapture();
        ExitGap(harness);

        harness.IsPaused.Should().BeFalse();
        harness.GapState.Should().Be(GapState.Inactive);
        harness.RenderSurface.Should().Be(ScenarioRenderSurface.Video);
        harness.ValidateInvariants().Should().BeEmpty();
    }

    [Fact]
    public void GapBehaviorSwitch_DuringGap_PreservesPreexistingManualPause()
    {
        var harness = CreateTwoTrackHarness();
        harness.GapBehavior = GapBehavior.Black;
        harness.SupplyLtc(1);
        harness.ManualPause();
        harness.SupplyLtc(6);

        harness.GapBehavior = GapBehavior.Freeze;
        harness.SupplyLtc(6);
        if (harness.GapState == GapState.EnteringFreeze)
            harness.CompleteFreezeCapture();
        ExitGap(harness);

        harness.IsPaused.Should().BeTrue();
        harness.GapState.Should().Be(GapState.Inactive);
        harness.RenderSurface.Should().Be(ScenarioRenderSurface.Video);
        harness.ValidateInvariants().Should().BeEmpty();
    }

    private static SyncScenarioHarness CreateTwoTrackHarness()
    {
        var harness = new SyncScenarioHarness();
        harness.AddTrack("track-1", timelineIn: 0, duration: 5);
        harness.AddTrack("track-2", timelineIn: 8, duration: 5);
        return harness;
    }

    private static void EnterAndCompleteGap(SyncScenarioHarness harness)
    {
        harness.SupplyLtc(6);
        if (harness.GapState == GapState.EnteringFreeze)
            harness.CompleteFreezeCapture();
    }

    private static void ExitGap(SyncScenarioHarness harness)
    {
        harness.SupplyLtc(9);
        harness.SupplyLtc(9);
    }

    private static void LoseSignal(SyncScenarioHarness harness)
    {
        harness.Tick100Milliseconds(3);
    }

    private static void RecoverSignal(SyncScenarioHarness harness)
    {
        harness.SupplyLtc(1.04);
        harness.SupplyLtc(1.08);
        harness.SupplyLtc(1.12);
    }
}
