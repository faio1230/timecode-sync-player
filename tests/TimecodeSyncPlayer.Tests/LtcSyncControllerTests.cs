using FluentAssertions;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

public sealed class LtcSyncControllerTests
{
    [Fact]
    public void ModeChange_RefreshesTrackLabel_WhenNoGapNeedsExiting()
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("track", 0);
        h.SetSyncEnabled(false);
        h.CurrentTrackLabels.Clear();

        h.ChangeMode(SyncMode.Single);

        h.CurrentTrackLabels.Should().ContainSingle().Which.Should().Be("1/1  track");
    }

    [Fact]
    public void DeviceEnumerationFailure_PersistsAcrossRepeatedTicks()
    {
        var h = new SyncScenarioHarness { IsMonitoring = false };
        h.Controller.DeviceEnumerationFailed();
        h.Tick100Milliseconds(8);
        h.DisplayStates[^1].FormatText.Should().Be("LTC デバイス列挙失敗");
    }

    [Fact]
    public void UnexpectedStop_ReentrantRunningChangePreservesErrorAndLossDetection()
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("track", 0);
        h.ManualPlay();
        h.SupplyLtc(1);
        h.Controller.MonitorStopped(new InvalidOperationException("device lost"));
        h.IsMonitoring.Should().BeFalse();
        h.DisplayStates[^1].FormatText.Should().Be("LTC 停止エラー");
        h.Tick100Milliseconds(3);
        h.DisplayStates[^1].FormatText.Should().Be("NO SIGNAL");
        h.IsPaused.Should().BeTrue();
    }

    [Fact]
    public void NormalStop_ClearsSignalLossAndFrameTextAcrossTicks()
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("track", 0);
        h.ManualPlay();
        h.SupplyLtc(1);
        h.Tick100Milliseconds(3);
        h.DisplayStates[^1].PauseReason.Should().Be("信号断で停止中");
        h.Controller.MonitorStopped(null);
        h.Tick100Milliseconds(5);
        h.DisplayStates[^1].FormatText.Should().Be("LTC 停止中");
        h.DisplayStates[^1].PauseReason.Should().BeEmpty();
        h.TimecodeText.Should().Be("--:--:--:--");
        h.RealTimeText.Should().Be("-.--- s");
    }

    [Fact]
    public void RawFrame_UsesReceiveTimestampBeforeDispatchAndDisplaysDiagnosticJumpWithoutRestoringSignal()
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("track", 0, 100);
        h.ManualPlay();
        h.Controller.ReceiveFrame(new(new LtcTimecode(0, 0, 1, 0, false), 25, 1), 10_000);
        h.Tick100Milliseconds(2);
        h.Controller.Tick(10_250);
        h.IsPaused.Should().BeTrue();
        h.Operations.Clear();
        h.Controller.ReceiveFrame(new(new LtcTimecode(0, 1, 10, 0, false), 25, 70), 10_260);
        h.Controller.LastLtcSeconds.Should().Be(70);
        h.TimecodeText.Should().Be("00:01:10:00");
        h.DisplayStates[^1].FormatText.Should().Be("NO SIGNAL");
        h.Operations.Should().NotContain(o => o.Name == "seek" || o.Name == "signal-loss-resume");
        h.Tick100Milliseconds();
        h.SupplyLtc(1.04);
        h.SupplyLtc(1.08);
        h.DisplayStates[^1].FormatText.Should().Be("NO SIGNAL", "the diagnostic jump must not count toward recovery");
        h.IsPaused.Should().BeTrue();
        h.SupplyLtc(1.12);
        h.IsPaused.Should().BeFalse();
    }

    [Fact]
    public void Recovery_ResumesBeforeSameFrameSyncAfterThreeValidFrames()
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("first", 0);
        h.AddTrack("next", 8);
        h.ManualPlay();
        h.SupplyLtc(1);
        h.Tick100Milliseconds(3);
        h.Operations.Clear();
        h.SupplyLtc(9);
        h.SupplyLtc(9.04);
        h.Operations.Should().BeEmpty();
        h.SupplyLtc(9.08);
        h.Operations.FindIndex(o => o.Name == "signal-loss-resume").Should().Be(0);
        h.Operations.FindIndex(o => o.Name == "loadfile").Should().BeGreaterThan(0);
        h.DisplayStates[^1].PauseReason.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SyncMode.Single)]
    [InlineData(SyncMode.Continue)]
    public void Seeking_SuppressesAutomaticSync(SyncMode mode)
    {
        var h = new SyncScenarioHarness();
        h.AddTrack("track", 0);
        h.ChangeMode(mode);
        h.BeginSeekBarInteraction();
        h.Operations.Clear();
        h.SupplyLtc(3);
        h.Operations.Should().BeEmpty();
    }

    [Fact]
    public void ManualGapExit_ClearsFrozenSurfaceAndRefreshesCurrentPositionOnce()
    {
        var h = new SyncScenarioHarness();
        h.ArrangeGapStateForModel(GapState.FreezeComplete);
        h.SetSyncEnabled(false);
        h.SetSyncEnabled(false);
        h.GapState.Should().Be(GapState.Inactive);
        h.RenderSurface.Should().Be(ScenarioRenderSurface.Video);
        h.Operations.Count(o => o.Name == "seek").Should().Be(1);
    }
}
