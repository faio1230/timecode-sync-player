namespace TimecodeSyncPlayer.Tests;

using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.IO;
using System.Reflection;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Tests.Helpers;

[Collection("Serilog global logger")]
public class TimecodeSyncServiceTests
{
    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    private sealed class LoggerCapture : IDisposable
    {
        private readonly ILogger _previous;

        public LoggerCapture(ListSink sink)
        {
            Sink = sink;
            _previous = Log.Logger;
            Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        }

        public ListSink Sink { get; }

        public List<LogEvent> Snapshot()
        {
            lock (Sink.Events) return Sink.Events.ToList();
        }

        public void Dispose() => Log.Logger = _previous;
    }

    private static LoggerCapture CaptureLogger() => new(new ListSink());

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

        public SyncDecision WhilePositionUntrusted(double ltcSeconds, SyncPlaybackState state)
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
            LastStatus = TimecodeSyncSeekPendingStatus.None;
        }

        public void BeginLoadWait(DateTime now)
        {
            HasPendingSeek = true;
            LastStatus = TimecodeSyncSeekPendingStatus.Pending;
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
        var seekState = new TimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.ReportSeekSent(10.0);

        service.EvaluateDecision(10.0, new SyncPlaybackState(true, true, false, 10.0, 100.0))
            .PositionUntrusted.Should().BeTrue();

        // v0.5.4 段 B: 着地は配信の世代と位置の事象で取る（旧 門 6 の窓と cooldown は畳んだ）。
        service.ObserveLandingState(
            new PlaybackPositionSample(10.0, PlaybackPositionBasis.Pipeline, 5, 10.05, 5, 5),
            toleranceSeconds: 0.2);

        SyncDecision result = service.EvaluateDecision(10.0,
            new SyncPlaybackState(true, true, false, 10.05, 100.0));

        result.PositionUntrusted.Should().BeFalse();
        engine.DecideCallCount.Should().Be(1);
        service.IsPlaybackPositionUsable.Should().BeTrue();
    }

    [Fact]
    public void EvaluateDecision_AfterTheSafetyTimeout_ResumesOnTheNextSample()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new TimecodeSyncSeekState(TimeSpan.FromSeconds(3));
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.ReportSeekSent(10.0);
        clock.Advance(TimeSpan.FromSeconds(3));

        // 位置が目標から離れている → 安全の時間切れ（観測で入る）。再確認の 3 サンプルは畳んだ。
        service.ObserveLandingState(
            new PlaybackPositionSample(5.0, PlaybackPositionBasis.Pipeline, 5, 5.0, 4, 5),
            toleranceSeconds: 0.2);
        seekState.LandingPhase.Should().Be(TimecodeSyncLandingPhase.FailedToLand);
        service.IsPlaybackPositionUsable.Should().BeTrue("着地せずでも判定は再開する（永久に止めない）");

        SyncDecision resumed = service.EvaluateDecision(5.0,
            new SyncPlaybackState(true, true, false, 5.0, 100.0));

        resumed.PositionUntrusted.Should().BeFalse();
        engine.DecideCallCount.Should().Be(1);
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
    public void TryMarkFileLoaded_WithoutLanding_ReturnsFalse()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        // v0.5.4 段 B3: ロードの成立は配信の世代の最初のフレーム（着地の事象）で決まる。進捗では解除しない。
        bool result = service.TryMarkFileLoaded(playbackSeconds: 12.12, renderedFrameCount: 5);

        result.Should().BeFalse();
        service.IsLoadingFile.Should().BeTrue();
    }

    [Fact]
    public void TryMarkFileLoaded_AfterLanding_ReturnsTrue()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);
        seekState.LastStatus = TimecodeSyncSeekPendingStatus.Settled;   // 着地の観測

        bool result = service.TryMarkFileLoaded(playbackSeconds: 12.12, renderedFrameCount: 5);

        result.Should().BeTrue();
        service.IsLoadingFile.Should().BeFalse();
    }

    [Fact]
    public void PollFileLoadRelease_ReturnsTrueOnlyOnTheReleaseTick()
    {
        // D20-b: 保持 LTC（Duplicate）でもロード解除だけを観測できる。
        // v0.5.4 段 B3: 解除は着地の事象（配信の世代の最初のフレーム）で決まる。
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        service.PollFileLoadRelease(playbackSeconds: 12.12, renderedFrameCount: 4).Should().BeFalse();
        seekState.LastStatus = TimecodeSyncSeekPendingStatus.Settled;   // 着地の観測
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
        seekState.LastStatus = TimecodeSyncSeekPendingStatus.Settled;   // 着地の観測

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
        seekState.LastStatus = TimecodeSyncSeekPendingStatus.Settled;   // 着地の観測
        service.TryMarkFileLoaded(playbackSeconds: 12.12, renderedFrameCount: 5).Should().BeTrue();

        service.BeginFileLoad(startPositionSeconds: 0.0, renderedFrameCount: 5);

        service.HasPendingFileLoadRelease.Should().BeFalse();
        service.PollFileLoadRelease(playbackSeconds: 0.1, renderedFrameCount: 6).Should().BeFalse();
    }

    [Fact]
    public void TryMarkFileLoaded_OnLanding_ReleasesWithoutWaitingForRenderedFrames()
    {
        // v0.5.4 段 B3: ロードの成立は着地の事象（配信の世代の最初のフレーム）で決まる。
        // 描画枚数・再生位置の進みは使わない（出荷構成の GPU 合成でも同じ）。
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 5.0, renderedFrameCount: 0);
        seekState.LastStatus = TimecodeSyncSeekPendingStatus.Settled;   // 着地の観測

        bool result = service.TryMarkFileLoaded(playbackSeconds: 5.0, renderedFrameCount: 0);

        result.Should().BeTrue();
        service.IsLoadingFile.Should().BeFalse();
    }

    [Fact]
    public void TryMarkFileLoaded_UpdatesDebounceTimestamp_WhenLanded()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);
        clock.Advance(TimeSpan.FromSeconds(1));
        seekState.LastStatus = TimecodeSyncSeekPendingStatus.Settled;   // 着地の観測

        service.TryMarkFileLoaded(playbackSeconds: 12.12, renderedFrameCount: 5);

        service.IsDebounced().Should().BeTrue();
    }

    [Fact]
    public void ShouldSuppressSeek_AfterStableFileLoad_DelegatesToSeekState()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState { ShouldSuppress = false };
        var service = new TimecodeSyncService(engine, seekState);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);
        seekState.LastStatus = TimecodeSyncSeekPendingStatus.Settled;   // 着地の観測（解除）
        service.TryMarkFileLoaded(playbackSeconds: 12.12, renderedFrameCount: 5);

        bool result = service.ShouldSuppressSeek(5.0, 0.2);

        result.Should().BeFalse();
        seekState.ShouldSuppressCalled.Should().BeTrue();
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
    public void TryMarkFileLoaded_ForcesRelease_AtTheLandingSafetyTimeout()
    {
        // v0.5.4 段 B3: 着地の事象が来ない（位置サンプルが取れない）ときだけ、安全の時間切れ
        // （3 秒。旧 門 7 の 2 秒と 門 18 の 5 秒をまとめた値）で解除する。
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        clock.Advance(TimecodeSyncSeekState.LandingSafetyTimeout);

        service.TryMarkFileLoaded(playbackSeconds: 12.0, renderedFrameCount: 3)
            .Should().BeTrue("着地が来なくても安全の時間切れで解除する");
        service.IsLoadingFile.Should().BeFalse();
        service.HasPendingFileLoadRelease.Should().BeTrue();
    }

    [Fact]
    public void TryMarkFileLoaded_BeforeTheSafetyTimeout_StillWaits()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        clock.Advance(TimecodeSyncSeekState.LandingSafetyTimeout - TimeSpan.FromTicks(1));

        service.TryMarkFileLoaded(playbackSeconds: 12.0, renderedFrameCount: 3)
            .Should().BeFalse("時間切れの前は着地を待つ");
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
    public void PollFileLoadRelease_WithoutNormalFrames_CollectsOnceRegardlessOfAge()
    {
        // v0.6.3 段 4（規則 4、旧 D35 の期限 1.5 秒の置き換え）: 解除の後に Normal が無ければ（マスターが止まっている）、
        // 年齢に依らず保持で 1 回だけ回収する。古い解除の再適用を塞ぐのは、Normal での破棄（下のテスト）。
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);
        clock.Advance(TimeSpan.FromSeconds(5));
        service.TryMarkFileLoaded(12.0, 3).Should().BeTrue();

        clock.Advance(TimeSpan.FromSeconds(1.5) + TimeSpan.FromTicks(1));

        service.PollFileLoadRelease(12.0, 3).Should().BeTrue("Normal が無いまま保持になったら、年齢に依らず 1 回だけ回収する");
        service.PollFileLoadRelease(12.0, 3).Should().BeFalse("回収は 1 回だけ");
        service.HasPendingFileLoadRelease.Should().BeFalse();
    }

    [Fact]
    public void DiscardPendingFileLoadRelease_ClearsThePendingRelease_SoTheNextPollIsFalse()
    {
        // v0.6.3 段 4（規則 4）: マスターが動いた（Normal・確定した Jump）ら、回収待ちの解除を捨てる。
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);
        clock.Advance(TimeSpan.FromSeconds(5));
        service.TryMarkFileLoaded(12.0, 3).Should().BeTrue();
        service.HasPendingFileLoadRelease.Should().BeTrue("前提: 回収待ちの解除がある");

        service.DiscardPendingFileLoadRelease();

        service.HasPendingFileLoadRelease.Should().BeFalse();
        service.PollFileLoadRelease(12.0, 3).Should().BeFalse("捨てた解除は回収しない");
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

    [Fact]
    public void SyncDisabled_DuringFileLoad_CancelsWithoutRelease()
    {
        // v0.5.3 段 3d: 同期を切った後に何秒待っても、ロード解除（file load released）が起きない
        // （§6 の 3）。v0.5.4 B6b: 着地窓を畳んだので、窓が開かないことの確認は外した。
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 12.0, renderedFrameCount: 3);

        using LoggerCapture capture = CaptureLogger();
        service.OnLifecycle(SyncLifecycleEvent.SyncDisabled);

        service.IsLoadingFile.Should().BeFalse("取り消しでロード中の印を下ろす");
        service.HasPendingFileLoadRelease.Should().BeFalse("解除の回収待ちも下ろす（解除ではない）");

        clock.Advance(TimeSpan.FromSeconds(30));

        service.TryMarkFileLoaded(playbackSeconds: 12.0, renderedFrameCount: 3)
            .Should().BeTrue("取り消し後はロード中ではない（解除もしない）");
        service.PollFileLoadRelease(playbackSeconds: 12.0, renderedFrameCount: 3)
            .Should().BeFalse("解除の回収は起きない");

        List<LogEvent> events = capture.Snapshot();
        events.Should().NotContain(e => e.MessageTemplate.Text.Contains("file load released"),
            "解除は起きない");
        events.Should().Contain(e => e.MessageTemplate.Text.Contains("file load cancelled by"),
            "取り消したときだけ 1 行残す");
    }

    [Fact]
    public void BeginFileLoad_ForgetsLastSettled_SoTheNextSeekIsNotSuppressed()
    {
        // v0.5.3 段 3f: 着地の直後に読み込むと、新しいファイルでの最初のシークが、前のファイルの
        // 着地目標による 0.5 秒の抑止を受けずに出る（§6 の 10）。
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        var engine = new MockSyncDecisionEngine();
        var seekState = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        var service = new TimecodeSyncService(engine, seekState, clock);

        // 10.0 へシークして着地させる（直前の着地の記録が残る）。
        service.ReportSeekSent(10.0);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        service.ShouldSuppressSeek(10.0, toleranceSeconds: 0.2).Should().BeTrue("前提: 着地の冷却中");
        clock.Advance(TimeSpan.FromMilliseconds(250));
        service.ShouldSuppressSeek(10.0, toleranceSeconds: 0.2).Should().BeTrue("前提: 着地を記録する");

        // 着地の直後に読み込み、ロードの解除（着地の事象）まで進める。
        service.BeginFileLoad(startPositionSeconds: 0.0, renderedFrameCount: 0);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        service.ObserveLandingState(
            new PlaybackPositionSample(0.2, PlaybackPositionBasis.Pipeline, 1, 0.2, 1, 1), 0.2);
        service.TryMarkFileLoaded(playbackSeconds: 0.2, renderedFrameCount: 10).Should().BeTrue();

        // 前のファイルの着地目標（10.0）のそばでも、最初のシークは抑止されない。
        service.ShouldSuppressSeek(10.0, toleranceSeconds: 0.2).Should().BeFalse(
            "読み込みで直前の着地の記録を忘れるので、0.5 秒待たずにシークできる");
    }

    // ---- v0.5.3 段 3g: ギャップの読み込みの口（ロード中の印を立てない） ----

    [Fact]
    public void BeginGapFreezeLoad_ClearsPendingSeekAndReleasePending_AndAdvancesEpoch()
    {
        // v0.5.3 段 3g: 口がするのは記録・読み込み番号・解除の回収待ち・シークの保留・着地の記録の 5 つ。
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        var engine = new MockSyncDecisionEngine();
        var seekState = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        var service = new TimecodeSyncService(engine, seekState, clock);
        service.BeginFileLoad(startPositionSeconds: 0.0, renderedFrameCount: 0);
        service.ObserveLandingState(
            new PlaybackPositionSample(0.2, PlaybackPositionBasis.Pipeline, 1, 0.2, 1, 1), 0.2);
        service.TryMarkFileLoaded(playbackSeconds: 0.2, renderedFrameCount: 10).Should().BeTrue();
        service.ReportSeekSent(10.0);
        service.HasPendingFileLoadRelease.Should().BeTrue("前提: 解除の回収待ち");
        service.SeekState.HasPendingSeek.Should().BeTrue("前提: 保留シーク");
        long epoch = service.FileLoadEpoch;

        service.BeginGapFreezeLoad("load-paused-at");

        service.FileLoadEpoch.Should().Be(epoch + 1, "読み込み番号を進める");
        service.HasPendingFileLoadRelease.Should().BeFalse("解除の回収待ちを下ろす");
        service.SeekState.HasPendingSeek.Should().BeTrue(
            "段 B: ギャップの読み込みも開始位置つきの読み込みなので、その読み込みの世代の着地待ちに入る");
    }

    [Fact]
    public void BeginGapFreezeLoad_DoesNotSetLoadingOrDebounce()
    {
        // v0.5.3 段 3g: ロード中の印を立てず、デバウンスも更新しない（設計 §1 の「しない」）。
        // v0.5.4 B6b: 着地窓を畳んだので、窓が開かないことの確認は外した。
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        var engine = new MockSyncDecisionEngine();
        var seekState = new TimecodeSyncSeekState(TimeSpan.FromSeconds(2));
        var service = new TimecodeSyncService(engine, seekState, clock);

        service.BeginGapFreezeLoad("load-paused-at");

        service.IsLoadingFile.Should().BeFalse("ロード中の印を立てない");
        service.IsDebounced().Should().BeFalse("デバウンスを更新しない");

        // 繰り返しても同じ（path-guard の 1 秒ごとの読み直し）。着地待ちは維持する。
        service.BeginGapFreezeLoad("path-guard");
        service.IsLoadingFile.Should().BeFalse();
        service.SeekState.HasPendingSeek.Should().BeTrue("読み込みの世代の着地待ちに入る");
    }

    [Fact]
    public void BeginGapFreezeLoad_RecordsTheEvent_ButDoesNotRaiseLifecycleRaised()
    {
        // v0.5.3 段 3g（親の承認）: コントローラの 3 ラッチ（jumpAppliedOnce・heldReapplyDone・
        // smoothUnavailable）はこの口では下ろさない（ギャップの読み込みは Jump の適用の途中で起きる）。
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);
        int raised = 0;
        service.LifecycleRaised += _ => raised++;

        using LoggerCapture capture = CaptureLogger();
        service.BeginGapFreezeLoad("path-guard");

        raised.Should().Be(0, "LifecycleRaised を上げない（コントローラのラッチを下ろさない）");
        List<LogEvent> events = capture.Snapshot();
        int lifecycleRows = events.Count(e =>
            e.MessageTemplate.Text.StartsWith("Sync lifecycle:", StringComparison.Ordinal) &&
            e.Properties.TryGetValue("Source", out LogEventPropertyValue? value) &&
            value is ScalarValue scalar && scalar.Value?.ToString() == "path-guard");
        lifecycleRows.Should().Be(1, "できごと GapFreezeLoad を source つきで 1 行残す");
    }

    // v0.6.3 段 1（観測）: relocate の行に出す先行量の出所。学習値 > スキャンの見積もり > なし。
    [Fact]
    public void RelocateLookaheadSource_IsLearnedThenHintThenNone()
    {
        var engine = new MockSyncDecisionEngine();
        var seekState = new MockTimecodeSyncSeekState();
        var service = new TimecodeSyncService(engine, seekState);

        service.RelocateLookaheadSource.Should().Be("none");
        service.SetSeekCostHintSeconds(0.2);
        service.RelocateLookaheadSource.Should().Be("hint");
        seekState.LearnedSeekDurationSeconds = 0.3;
        service.RelocateLookaheadSource.Should().Be("learned");
        service.RelocateLookaheadSeconds.Should().BeApproximately(0.3, 1e-9);
    }
}
