namespace TimecodeSyncPlayer.Tests;

using FluentAssertions;
using System.IO;
using System.Reflection;
using TimecodeSyncPlayer.Tests.Helpers;

public class TimecodeSyncServiceTests
{
    private class MockSyncDecisionEngine : ISyncDecisionEngine
    {
        public SyncDecision DecisionToReturn { get; set; } = SyncDecision.None;
        public double LastLtcSeconds { get; private set; }
        public SyncPlaybackState? LastState { get; private set; }
        public int DecideCallCount { get; private set; }
        public int UntrustedCallCount { get; private set; }
        public List<double> SeekCosts { get; } = new();
        public SyncDecision UntrustedDecision { get; set; } = new(
            SyncActionType.None, 0.0, 0.0, 0.2, 30.0, 30.0, false, false, PositionUntrusted: true);

        public SyncDecision Decide(double ltcSeconds, SyncPlaybackState state)
        {
            DecideCallCount++;
            LastLtcSeconds = ltcSeconds;
            LastState = state;
            return DecisionToReturn;
        }

        public void UpdateSeekCostSeconds(double seconds) => SeekCosts.Add(seconds);

        public SyncDecision WhilePositionUntrusted(SyncPlaybackState state)
        {
            UntrustedCallCount++;
            return UntrustedDecision;
        }
    }

    private class MockTimecodeSyncSeekState : ITimecodeSyncSeekState
    {
        public bool HasPendingSeek { get; set; }
        public double TargetSeconds { get; set; }
        public TimecodeSyncSeekPendingStatus LastStatus { get; set; } = TimecodeSyncSeekPendingStatus.None;
        public double? LearnedSeekDurationSeconds { get; set; }
        public int ResetLearningCallCount { get; private set; }

        public void ResetLearning()
        {
            ResetLearningCallCount++;
            LearnedSeekDurationSeconds = null;
        }

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

        public bool ShouldSuppressSeek(double playbackSeconds, double toleranceSeconds, DateTime now,
            double requestedTargetSeconds = double.NaN)
        {
            ShouldSuppressCalled = true;
            return ShouldSuppress;
        }
    }

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

    // ---- D37-b: シーク中の位置を信用しない ----

    [Fact]
    public void EvaluateDecision_WhilePending_DoesNotCallEngine()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.ReportSeekSent(10.0);
        seekState.HasPendingSeek = true;

        SyncDecision result = service.EvaluateDecision(10.0,
            new SyncPlaybackState(true, true, false, PlaybackSeconds: 5.0, DurationSeconds: 100.0));

        result.PositionUntrusted.Should().BeTrue();
        result.Action.Should().Be(SyncActionType.None);
        engine.DecideCallCount.Should().Be(0);
        engine.UntrustedCallCount.Should().Be(1);
        service.IsPlaybackPositionUsable.Should().BeFalse();
    }

    [Fact]
    public void EvaluateDecision_AfterSettledLanding_ResumesEngine()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.ReportSeekSent(10.0);

        service.EvaluateDecision(10.0, new SyncPlaybackState(true, true, false, 10.0, 100.0))
            .PositionUntrusted.Should().BeTrue();
        service.ShouldSuppressSeek(10.05, toleranceSeconds: 0.2);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        service.ShouldSuppressSeek(10.05, toleranceSeconds: 0.2);

        SyncDecision result = service.EvaluateDecision(10.0,
            new SyncPlaybackState(true, true, false, 10.05, 100.0));

        result.PositionUntrusted.Should().BeFalse();
        engine.DecideCallCount.Should().Be(1);
        service.IsPlaybackPositionUsable.Should().BeTrue();
    }

    [Fact]
    public void EvaluateDecision_AfterTimeout_RequiresStableSamples()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.ReportSeekSent(10.0);
        clock.Advance(TimeSpan.FromSeconds(3));

        // 位置が目標から離れている → 時間切れで解除 → 位置の再確認が必要。
        service.ShouldSuppressSeek(5.0, toleranceSeconds: 0.2);
        service.EvaluateDecision(5.0, new SyncPlaybackState(true, true, false, 5.0, 100.0))
            .PositionUntrusted.Should().BeTrue();

        double position = 5.0;
        for (int i = 1; i <= 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            position += 0.1;
            service.EvaluateDecision(5.0, new SyncPlaybackState(true, true, false, position, 100.0))
                .PositionUntrusted.Should().BeTrue($"位置の再確認中 {i} サンプル目");
        }

        clock.Advance(TimeSpan.FromMilliseconds(100));
        position += 0.1;
        SyncDecision resumed = service.EvaluateDecision(5.0,
            new SyncPlaybackState(true, true, false, position, 100.0));

        resumed.PositionUntrusted.Should().BeFalse();
        engine.DecideCallCount.Should().Be(1);
    }

    [Fact]
    public void EvaluateDecision_AfterLanding_DisallowsRateCatchUpForOneSecond()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        var state = new SyncPlaybackState(true, true, false, 10.0, 100.0);

        service.NotifyLanding();
        service.EvaluateDecision(10.0, state);
        engine.LastState!.RateCatchUpAllowed.Should().BeFalse("ギャップ明け・切替の着地直後はシークで詰める");

        clock.Advance(TimeSpan.FromMilliseconds(1100));
        service.EvaluateDecision(10.0, state);
        engine.LastState!.RateCatchUpAllowed.Should().BeTrue("着地から 1 秒を過ぎたら速度補正優先に戻る");
    }

    [Fact]
    public void BeginFileLoad_StartsTheLandingWindow()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);

        service.BeginFileLoad(startPositionSeconds: 10.0, renderedFrameCount: 0);
        service.EvaluateDecision(10.0, new SyncPlaybackState(true, true, false, 10.0, 100.0));

        engine.LastState!.RateCatchUpAllowed.Should().BeFalse("トラック切替のロード直後も着地として扱う");
    }

    [Fact]
    public void EvaluateDecision_PublishesLearnedSeekCost()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState { LearnedSeekDurationSeconds = 0.6 };
        var service = new TimecodeSyncService(engine, seekState);

        service.EvaluateDecision(10.0, new SyncPlaybackState(true, true, false, 10.0, 100.0));

        engine.SeekCosts.Should().Equal(new[] { 0.6 });
    }

    [Fact]
    public void ReportSeekSent_SetsSeekStatePending()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 12, 34, 56, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);

        service.ReportSeekSent(12.5);

        seekState.BeginSeekCallCount.Should().Be(1);
        seekState.LastBeginSeekTarget.Should().Be(12.5);
        seekState.LastBeginSeekSentAt.Should().Be(new DateTime(2026, 9, 7, 12, 34, 56, DateTimeKind.Utc));
        seekState.HasPendingSeek.Should().BeTrue();
    }

    [Fact]
    public void ReportSeekSent_DebouncesBeforeNextSeek()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);

        service.ReportSeekSent(12.5);

        service.IsDebounced().Should().BeTrue();
    }

    [Theory]
    [InlineData(2_499_999, true)]
    [InlineData(2_500_000, false)]
    [InlineData(2_500_001, false)]
    public void IsDebounced_UsesInjectedClockAt250MillisecondBoundary(long elapsedTicks, bool expected)
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.ReportSeekSent(12.5);

        clock.Advance(TimeSpan.FromTicks(elapsedTicks));

        service.IsDebounced().Should().Be(expected);
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
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);

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
    public void PollFileLoadRelease_ReturnsTrueOnlyOnTheReleaseTick()
    {
        // D20-b: 保持 LTC（Duplicate）でもロード解除だけを観測できる。
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        service.PollFileLoadRelease(playbackSeconds: 12.12, renderedFrameCount: 4).Should().BeFalse();
        service.PollFileLoadRelease(playbackSeconds: 12.12, renderedFrameCount: 5).Should().BeTrue();
        service.PollFileLoadRelease(playbackSeconds: 12.2, renderedFrameCount: 6).Should().BeFalse();
        service.IsLoadingFile.Should().BeFalse();
    }

    [Fact]
    public void PollFileLoadRelease_WhenAnotherCallerReleasedTheLoad_ReturnsTrueOnce()
    {
        // D27-b: 解除が同期コーディネーター側の完了で先に起きても、保持 LTC の再適用が
        // 回収できるようにする（S-4 の取りこぼし）。
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        service.TryMarkFileLoaded(playbackSeconds: 12.12, renderedFrameCount: 5).Should().BeTrue();
        service.IsLoadingFile.Should().BeFalse();
        service.HasPendingFileLoadRelease.Should().BeTrue();

        service.PollFileLoadRelease(playbackSeconds: 12.2, renderedFrameCount: 6).Should().BeTrue();
        service.HasPendingFileLoadRelease.Should().BeFalse();
        service.PollFileLoadRelease(playbackSeconds: 12.3, renderedFrameCount: 7).Should().BeFalse();
    }

    [Fact]
    public void BeginFileLoad_ClearsPendingFileLoadRelease()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);
        service.TryMarkFileLoaded(playbackSeconds: 12.12, renderedFrameCount: 5).Should().BeTrue();

        service.BeginFileLoad(startPositionSeconds: 0.0, renderedFrameCount: 5);

        service.HasPendingFileLoadRelease.Should().BeFalse();
        service.PollFileLoadRelease(playbackSeconds: 0.1, renderedFrameCount: 6).Should().BeFalse();
    }

    [Fact]
    public void TryMarkFileLoaded_GpuCompositing_OpensOnPublishedFrames_WithoutWaitingForCpuBitmaps()
    {
        // 出荷構成（GPU 合成）: 表示経路（OutputEngine）の公開数だけが進む。
        long publishedFrames = 0;
        var counter = new RenderedFrameCounter(gpuPublishedFrames: () => publishedFrames);

        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 5.0, renderedFrameCount: counter.Read());

        publishedFrames += 2;                              // 表示経路へ 2 フレーム公開
        clock.Advance(TimeSpan.FromMilliseconds(120));     // 5 秒のタイムアウトには達しない

        bool result = service.TryMarkFileLoaded(playbackSeconds: 5.12, renderedFrameCount: counter.Read());

        result.Should().BeTrue();
        service.IsLoadingFile.Should().BeFalse();
    }

    [Fact]
    public void TryMarkFileLoaded_UpdatesDebounceTimestamp_WhenStable()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);
        clock.Advance(TimeSpan.FromSeconds(1));

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

    [Theory]
    [InlineData(49_999_999, true, true)]
    [InlineData(50_000_000, true, true)]
    [InlineData(50_000_001, false, false)]
    public void ShouldSuppressSeek_UsesInjectedClockAtFiveSecondLoadTimeoutBoundary(
        long elapsedTicks,
        bool expectedSuppression,
        bool expectedLoading)
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState { ShouldSuppress = false };
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        clock.Advance(TimeSpan.FromTicks(elapsedTicks));

        service.ShouldSuppressSeek(0.0, 0.2).Should().Be(expectedSuppression);
        service.IsLoadingFile.Should().Be(expectedLoading);
    }

    [Fact]
    public void ShouldSuppressSeek_AfterTimeout_UpdatesDebounce()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState { ShouldSuppress = false };
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        clock.Advance(TimeSpan.FromSeconds(5) + TimeSpan.FromTicks(1));

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
    public void TryMarkFileLoaded_ForcesRelease_WhenProgressStallsPastTheGrace()
    {
        // D35: 停止（保持）などで描画フレーム・再生位置が進まなくても、ロード開始から
        // 一定時間（5 秒。実素材のプロファイル試行 2.2〜2.5 秒を下回らない）で解除する。
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        clock.Advance(TimeSpan.FromSeconds(5));

        service.TryMarkFileLoaded(playbackSeconds: 12.0, renderedFrameCount: 3)
            .Should().BeTrue("進捗が無くても期限で解除する");
        service.IsLoadingFile.Should().BeFalse();
        service.HasPendingFileLoadRelease.Should().BeTrue();
    }

    [Fact]
    public void TryMarkFileLoaded_BeforeTheGrace_StillWaitsForProgress()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));

        service.TryMarkFileLoaded(playbackSeconds: 12.0, renderedFrameCount: 3)
            .Should().BeFalse("期限前は従来どおり進捗を待つ");
        service.IsLoadingFile.Should().BeTrue();
    }

    [Fact]
    public void PollFileLoadRelease_ConsumesFreshReleaseOnce()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);
        clock.Advance(TimeSpan.FromSeconds(5));
        service.TryMarkFileLoaded(12.0, 3).Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(1));  // 鮮度（1.5 秒）以内

        service.PollFileLoadRelease(12.0, 3).Should().BeTrue();
        service.PollFileLoadRelease(12.0, 3).Should().BeFalse();
    }

    [Fact]
    public void PollFileLoadRelease_DropsStaleRelease()
    {
        // D35: ロード直後の 1 回だけを対象にし、数秒前に解除された値を保持開始時に
        // 再適用して同期を壊さない。
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);
        clock.Advance(TimeSpan.FromSeconds(5));
        service.TryMarkFileLoaded(12.0, 3).Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(1.5) + TimeSpan.FromTicks(1));

        service.PollFileLoadRelease(12.0, 3).Should().BeFalse("古い解除は再適用しない");
        service.HasPendingFileLoadRelease.Should().BeFalse();
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
