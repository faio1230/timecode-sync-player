using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

public class SyncScenarioHarnessTests
{
    [Fact]
    public void SignalLossAndRecovery_RecordsLtcDisplayTransitions()
    {
        var harness = new SyncScenarioHarness
        {
            SignalLossMode = LtcSignalLossMode.RunThrough,
        };
        harness.AddTrack("track-1", timelineIn: 0);

        harness.SupplyLtc(1);
        harness.Tick100Milliseconds(3);
        harness.SupplyLtc(1.1);
        harness.SupplyLtc(1.2);
        harness.SupplyLtc(1.3);

        harness.DisplayStates.Should().ContainInOrder(
            new ScenarioLtcDisplayState("fps: 25", "#55D86A"),
            new ScenarioLtcDisplayState("NO SIGNAL", "#666666"),
            new ScenarioLtcDisplayState("fps: 25", "#55D86A"));
    }

    [Fact]
    public void HarnessSelfTest_LtcOnTrack_UsesRealPlaylistAndCoordinatorToLoadTrack()
    {
        var harness = new SyncScenarioHarness();
        PlaylistTrack track = harness.AddTrack("track-1", timelineIn: 0);

        harness.SupplyLtc(1);

        harness.LoadedTrackId.Should().Be(track.Id);
        harness.Operations.Should().ContainSingle(operation =>
            operation.Name == "loadfile" && operation.Text == track.FilePath && operation.Value == 1);
    }

    [Fact]
    public void ManualPause_RemainsPaused_WhenReturningFromFreezeGap()
    {
        var harness = new SyncScenarioHarness { GapBehavior = GapBehavior.Freeze };
        harness.AddTrack("track-1", timelineIn: 0, duration: 5);
        harness.AddTrack("track-2", timelineIn: 8, duration: 5);
        harness.SupplyLtc(1);
        harness.ManualPause();

        harness.SupplyLtc(6);
        harness.IsPaused.Should().BeTrue("gap pause must not take ownership from a manual pause");
        harness.CompleteFreezeCapture();

        harness.SupplyLtc(9);

        harness.IsPaused.Should().BeTrue("manual playback control must always win");
    }
}
