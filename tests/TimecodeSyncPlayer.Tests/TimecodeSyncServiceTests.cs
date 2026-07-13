namespace TimecodeSyncPlayer.Tests;

using FluentAssertions;
using System.IO;
using System.Reflection;

public class TimecodeSyncServiceTests
{
    private class MockSyncDecisionEngine : ISyncDecisionEngine
    {
        public SyncDecision DecisionToReturn { get; set; } = SyncDecision.None;
        public double LastLtcSeconds { get; private set; }
        public SyncPlaybackState? LastState { get; private set; }

        public SyncDecision Decide(double ltcSeconds, SyncPlaybackState state)
        {
            LastLtcSeconds = ltcSeconds;
            LastState = state;
            return DecisionToReturn;
        }
    }

    private class MockTimecodeSyncSeekState : ITimecodeSyncSeekState
    {
        public bool HasPendingSeek { get; set; }
        public double TargetSeconds { get; set; }
        public TimecodeSyncSeekPendingStatus LastStatus { get; set; } = TimecodeSyncSeekPendingStatus.None;

        public double LastBeginSeekTarget { get; private set; }
        public DateTime LastBeginSeekSentAt { get; private set; }
        public int BeginSeekCallCount { get; private set; }
        public int ClearCallCount { get; private set; }

        public bool ShouldSuppress { get; set; }
        public bool ShouldSuppressCalled { get; private set; }

        public void BeginSeek(double targetSeconds, DateTime sentAt)
        {
            LastBeginSeekTarget = targetSeconds;
            LastBeginSeekSentAt = sentAt;
            BeginSeekCallCount++;
            HasPendingSeek = true;
            TargetSeconds = targetSeconds;
        }

        public void Clear()
        {
            ClearCallCount++;
            HasPendingSeek = false;
        }

        public bool ShouldSuppressSeek(double playbackSeconds, double toleranceSeconds, DateTime now)
        {
            ShouldSuppressCalled = true;
            return ShouldSuppress;
        }
    }

    private static readonly FieldInfo s_lastSyncSeekAtField =
        typeof(TimecodeSyncService).GetField("_lastSyncSeekAt",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo s_lastLoggedSyncActionField =
        typeof(TimecodeSyncService).GetField("_lastLoggedSyncAction",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    [Fact]
    public void ContinueModeSyncPlaybackState_DoesNotTreatPendingSyncSeekAsUserSeek()
    {
        string sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "TimecodeSyncPlayer", "MainWindow.xaml.cs"));
        string source = File.ReadAllText(sourcePath);

        source.Should().NotContain(
            "IsSeeking: _seeking || _syncService.SeekState.HasPendingSeek",
            "保留中の同期seekをIsSeekingへ混ぜるとSyncDecisionEngineがNoneを返し、pending解除と次のタイムコードジャンプseekに到達できなくなる");
    }

    [Fact]
    public void EvaluateDecision_DelegatesToEngine_AndReturnsDecision()
    {
        var engine = new MockSyncDecisionEngine
        {
            DecisionToReturn = new SyncDecision(
                SyncActionType.Seek, 15.0, 5.0, 0.2, 30.0, 30.0, false, false)
        };
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);

        var state = new SyncPlaybackState(true, true, false, 10.0, 100.0);
        SyncDecision result = service.EvaluateDecision(10.0, state);

        result.Action.Should().Be(SyncActionType.Seek);
        result.TargetSeconds.Should().Be(15.0);
        result.DeltaSeconds.Should().Be(5.0);
    }

    [Fact]
    public void EvaluateDecision_CallsEngineWithCorrectState()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);

        var state = new SyncPlaybackState(true, true, false, 10.0, 100.0, 24.0, 25.0);
        service.EvaluateDecision(42.5, state);

        engine.LastLtcSeconds.Should().Be(42.5);
        engine.LastState.Should().Be(state);
    }

    [Fact]
    public void ReportSeekSent_SetsSeekStatePending()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);

        service.ReportSeekSent(12.5);

        seekState.BeginSeekCallCount.Should().Be(1);
        seekState.LastBeginSeekTarget.Should().Be(12.5);
        seekState.HasPendingSeek.Should().BeTrue();
    }

    [Fact]
    public void ReportSeekSent_DebouncesBeforeNextSeek()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);

        service.ReportSeekSent(12.5);

        service.IsDebounced().Should().BeTrue();
    }

    [Fact]
    public void IsDebounced_ReturnsFalse_AfterSufficientTime()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);

        s_lastSyncSeekAtField.SetValue(service, DateTime.UtcNow.AddMilliseconds(-500));

        service.IsDebounced().Should().BeFalse();
    }

    [Fact]
    public void BeginFileLoad_SetsLoadingFlag()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);

        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        service.IsLoadingFile.Should().BeTrue();
    }

    [Fact]
    public void BeginFileLoad_ClearsSeekState()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState { HasPendingSeek = true };
        var service = new TimecodeSyncService(engine, seekState);

        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        seekState.ClearCallCount.Should().Be(1);
    }

    [Fact]
    public void BeginFileLoad_UpdatesDebounceTimestamp()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);
        s_lastSyncSeekAtField.SetValue(service, DateTime.MinValue);

        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        service.IsDebounced().Should().BeTrue();
    }

    [Fact]
    public void ShouldSuppressSeek_ReturnsTrueWhileFileLoading()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState { ShouldSuppress = false };
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        bool result = service.ShouldSuppressSeek(0.0, 0.2);

        result.Should().BeTrue();
    }

    [Fact]
    public void TryMarkFileLoaded_ReturnsFalse_WhenPlaybackHasNotAdvanced()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        bool result = service.TryMarkFileLoaded(playbackSeconds: 12.02, renderedFrameCount: 5);

        result.Should().BeFalse();
        service.IsLoadingFile.Should().BeTrue();
    }

    [Fact]
    public void TryMarkFileLoaded_ReturnsFalse_WhenNewFramesHaveNotRendered()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        bool result = service.TryMarkFileLoaded(playbackSeconds: 12.12, renderedFrameCount: 4);

        result.Should().BeFalse();
        service.IsLoadingFile.Should().BeTrue();
    }

    [Fact]
    public void TryMarkFileLoaded_ClearsLoadingFlag_WhenPlaybackAndFramesAreStable()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        bool result = service.TryMarkFileLoaded(playbackSeconds: 12.12, renderedFrameCount: 5);

        result.Should().BeTrue();
        service.IsLoadingFile.Should().BeFalse();
    }

    [Fact]
    public void TryMarkFileLoaded_UpdatesDebounceTimestamp_WhenStable()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);
        s_lastSyncSeekAtField.SetValue(service, DateTime.MinValue);

        service.TryMarkFileLoaded(playbackSeconds: 12.12, renderedFrameCount: 5);

        service.IsDebounced().Should().BeTrue();
    }

    [Fact]
    public void ShouldSuppressSeek_BeforeStableFileLoad_DoesNotDelegateToSeekState()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState { ShouldSuppress = false };
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);
        service.TryMarkFileLoaded(playbackSeconds: 12.12, renderedFrameCount: 4);

        bool result = service.ShouldSuppressSeek(5.0, 0.2);

        result.Should().BeTrue();
        seekState.ShouldSuppressCalled.Should().BeFalse();
    }

    [Fact]
    public void ShouldSuppressSeek_AfterStableFileLoad_DelegatesToSeekState()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState { ShouldSuppress = false };
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);
        service.TryMarkFileLoaded(playbackSeconds: 12.12, renderedFrameCount: 5);

        bool result = service.ShouldSuppressSeek(5.0, 0.2);

        result.Should().BeFalse();
        seekState.ShouldSuppressCalled.Should().BeTrue();
    }

    [Fact]
    public void ShouldSuppressSeek_AutoClearsLoadingAfterTimeout()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState { ShouldSuppress = false };
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        var fileLoadStartedAtField = typeof(TimecodeSyncService)
            .GetField("_fileLoadStartedAt", BindingFlags.NonPublic | BindingFlags.Instance)!;
        fileLoadStartedAtField.SetValue(service, DateTime.UtcNow.AddSeconds(-6));

        bool result = service.ShouldSuppressSeek(0.0, 0.2);

        result.Should().BeFalse();
        service.IsLoadingFile.Should().BeFalse();
    }

    [Fact]
    public void ShouldSuppressSeek_AfterTimeout_UpdatesDebounce()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState { ShouldSuppress = false };
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        // タイムアウト（6秒前に設定）
        var fileLoadStartedAtField = typeof(TimecodeSyncService)
            .GetField("_fileLoadStartedAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        fileLoadStartedAtField.SetValue(service, DateTime.UtcNow.AddSeconds(-6));

        service.ShouldSuppressSeek(0.0, 0.2);    // タイムアウトを発火させる

        service.IsDebounced().Should().BeTrue();    // デバウンスが更新されていること
    }

    [Fact]
    public void ShouldSuppressSeek_ReturnsFalse_WhenNoPendingSeek()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState { ShouldSuppress = false };
        var service = new TimecodeSyncService(engine, seekState);

        bool result = service.ShouldSuppressSeek(5.0, 0.2);

        result.Should().BeFalse();
        seekState.ShouldSuppressCalled.Should().BeTrue();
    }

    [Fact]
    public void ShouldSuppressSeek_ReturnsTrue_WhenPendingAndWithinTolerance()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState { ShouldSuppress = true };
        var service = new TimecodeSyncService(engine, seekState);

        bool result = service.ShouldSuppressSeek(5.0, 0.2);

        result.Should().BeTrue();
    }

    [Fact]
    public void ClearSeekState_ResetsPendingSeek()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);

        service.ClearSeekState();

        seekState.ClearCallCount.Should().Be(1);
    }

    [Fact]
    public void SeekState_ExposesUnderlyingSeekState()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);

        service.SeekState.Should().BeSameAs(seekState);
    }

    [Fact]
    public void EvaluateDecision_LogsOnlyWhenActionChanges()
    {
        var engine = new MockSyncDecisionEngine
        {
            DecisionToReturn = new SyncDecision(
                SyncActionType.Seek, 15.0, 5.0, 0.2, 30.0, 30.0, false, false)
        };
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);
        var state = new SyncPlaybackState(true, true, false, 10.0, 100.0);

        service.EvaluateDecision(10.0, state);
        s_lastLoggedSyncActionField.GetValue(service).Should().Be(SyncActionType.Seek);

        service.EvaluateDecision(10.0, state);
        s_lastLoggedSyncActionField.GetValue(service).Should().Be(SyncActionType.Seek);

        engine.DecisionToReturn = SyncDecision.None;
        service.EvaluateDecision(10.0, state);
        s_lastLoggedSyncActionField.GetValue(service).Should().Be(SyncActionType.None);
    }
}
