using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public class ContinueOnTrackCoordinatorTests
{
    private static TimecodeSyncService CreateService()
    {
        // T9: 製品既定は無効のため、先行補償を使うテストは明示的に有効化した共有インスタンスを渡す。
        var compensator = new SeekLatencyCompensator(enabled: true);
        return new TimecodeSyncService(
            new SyncDecisionEngine(new SyncDecisionOptions(), compensator),
            new TimecodeSyncSeekState(), null, compensator);
    }

    private static FileLoadStabilityLogState CreateLogState() =>
        new(TimeSpan.FromSeconds(1));

    private static PlaylistTrack CreateTrack(Guid id, string name = "track", string path = "C:/clip.mp4") =>
        new(
            Id: id,
            FilePath: path,
            Name: name,
            MediaIn: TimeSpan.Zero,
            MediaOut: null,
            TimelineOffset: TimeSpan.Zero,
            MediaDuration: TimeSpan.FromSeconds(200),
            SyncOffset: TimeSpan.Zero,
            FrameRate: 30.0,
            IsEnabled: true);

    // SyncEnabled + CurrentTrack + not seeking + finite + usable duration → Seek になり得る状態
    private static SyncPlaybackState SeekYieldingState(double playbackSeconds) => new(
        SyncEnabled: true,
        HasCurrentTrack: true,
        IsSeeking: false,
        PlaybackSeconds: playbackSeconds,
        DurationSeconds: 200.0,
        VideoFps: 30.0,
        TimecodeFps: 30.0);

    private static SyncPlaybackState NoSeekState(double playbackSeconds) => new(
        SyncEnabled: false,
        HasCurrentTrack: true,
        IsSeeking: false,
        PlaybackSeconds: playbackSeconds,
        DurationSeconds: 200.0,
        VideoFps: 30.0,
        TimecodeFps: 30.0);

    /// <summary>全デリゲート呼び出しを記録し、返り値を設定可能なフェイク。</summary>
    private sealed class Recorder
    {
        public readonly List<string> Calls = new();
        public readonly List<double> SeekTargets = new();
        public readonly List<(string path, double start)> LoadFileArgs = new();
        public readonly List<Guid> SetLoadedTrackIds = new();

        public GapExitActionType GapExit = GapExitActionType.None;
        public bool SeekResult = true;
        public bool LoadFileResult = true;
        public bool NativeSeeking;
        public Guid? LoadedTrackId;
        public long TotalRenderedFrames;
        public (int rc, double playbackSeconds) TimePos = (0, 1.0);
        public Func<double, SyncPlaybackState> BuildState = SeekYieldingState;

        private static SyncPositionRead ToRead((int rc, double playbackSeconds) timePos) =>
            timePos.rc == 0 ? new SyncPositionRead(true, timePos.playbackSeconds) : SyncPositionRead.Failed;

        public ContinueOnTrackEffects Build() => new(
            PeekGapExit: () => new GapExitAction(GapExit),
            IsPlaybackPaused: () => true,
            ClearGapFreezeFrame: () => Calls.Add("ClearGapFreezeFrame"),
            DecideGapExit: () => { Calls.Add("DecideGapExit"); return new GapExitAction(GapExit); },
            SeekTo: target => { Calls.Add("SeekTo"); SeekTargets.Add(target); return SeekResult; },
            ResumePlayback: () => Calls.Add("ResumePlayback"),
            ApplyPauseState: paused => Calls.Add($"ApplyPauseState({paused})"),
            UpdateCurrentTrackLabel: () => Calls.Add("UpdateCurrentTrackLabel"),
            GetLoadedTrackId: () => { Calls.Add("GetLoadedTrackId"); return LoadedTrackId; },
            SetLoadedTrackId: id => { Calls.Add("SetLoadedTrackId"); SetLoadedTrackIds.Add(id); LoadedTrackId = id; },
            LoadFile: (path, start) => { Calls.Add("LoadFile"); LoadFileArgs.Add((path, start)); return LoadFileResult; },
            GetTotalRenderedFrames: () => { Calls.Add("GetTotalRenderedFrames"); return TotalRenderedFrames; },
            ReadPosition: () => { Calls.Add("ReadPosition"); return ToRead(TimePos); },
            BuildPlaybackState: ps => { Calls.Add("BuildPlaybackState"); return BuildState(ps); },
            IsNativeSeeking: () => NativeSeeking);
    }

    private static TimelineQueryResult OnTrack(PlaylistTrack track, double mediaPos) =>
        new(TimelineQueryStatus.OnTrack, track, mediaPos, null);

    [Fact]
    public void SameTrack_NativeSeeking_DoesNotSettleSyntheticTarget_AndResumesLatestRequestAfterCompletion()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = new TimecodeSyncService(new SyncDecisionEngine(), new TimecodeSyncSeekState(), clock);
        service.ReportSeekSent(10);
        var track = CreateTrack(Guid.NewGuid());
        var rec = new Recorder { LoadedTrackId = track.Id, NativeSeeking = true, TimePos = (0, 10) };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(track, 10), 10).Should().Be(SyncRequestResult.Deferred);
        clock.Advance(TimeSpan.FromSeconds(3));
        coordinator.Handle(OnTrack(track, 10), 10).Should().Be(SyncRequestResult.Deferred);
        coordinator.Handle(OnTrack(track, 30), 30).Should().Be(SyncRequestResult.Deferred);
        rec.Calls.Should().NotContain(new[] { "ReadPosition", "GetTotalRenderedFrames", "BuildPlaybackState" });
        service.SeekState.HasPendingSeek.Should().BeTrue();
        service.SeekState.TargetSeconds.Should().Be(10);
        service.SeekState.LastStatus.Should().Be(TimecodeSyncSeekPendingStatus.Pending);
        rec.SeekTargets.Should().BeEmpty();

        rec.NativeSeeking = false;
        rec.TimePos = (0, 11);
        // D37-b: セトル窓内 → 時間切れ → 位置が安定するまで（3 サンプル）判定しない。
        coordinator.Handle(OnTrack(track, 30), 30).Should().Be(SyncRequestResult.Deferred);
        for (int i = 1; i <= 4; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            rec.TimePos = (0, 11 + i * 0.1);
            coordinator.Handle(OnTrack(track, 30), 30).Should().Be(SyncRequestResult.Deferred, $"位置の再確認中 {i} サンプル目");
        }
        // 再確認が完了した次のフレームで、新しい要求（30）が発行される。
        clock.Advance(TimeSpan.FromMilliseconds(100));
        rec.TimePos = (0, 11.6);
        coordinator.Handle(OnTrack(track, 30), 30).Should().Be(SyncRequestResult.Complete);
        rec.SeekTargets.Should().Equal(30);
        service.SeekState.TargetSeconds.Should().Be(30);
    }

    [Fact]
    public void SameTrack_NativeSeeking_DoesNotMarkFileLoadedFromSyntheticProgress()
    {
        var track = CreateTrack(Guid.NewGuid());
        var service = CreateService();
        service.BeginFileLoad(10, 0);
        var rec = new Recorder
        {
            LoadedTrackId = track.Id, NativeSeeking = true,
            TimePos = (0, 11), TotalRenderedFrames = 10
        };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(track, 11), 11).Should().Be(SyncRequestResult.Deferred);
        service.IsLoadingFile.Should().BeTrue();

        rec.NativeSeeking = false;
        coordinator.Handle(OnTrack(track, 11), 11).Should().Be(SyncRequestResult.Complete);
        service.IsLoadingFile.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchTrack_NativeSeeking_AllowsNewClipToOverride(bool exitingGap)
    {
        var track = CreateTrack(Guid.NewGuid(), path: "C:/next.mp4");
        var rec = new Recorder
        {
            LoadedTrackId = Guid.NewGuid(), NativeSeeking = true,
            GapExit = exitingGap ? GapExitActionType.ResumePlayback : GapExitActionType.None
        };
        var coordinator = new ContinueOnTrackCoordinator(CreateService(), CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(track, 12.5), 12.5).Should().Be(SyncRequestResult.Complete);

        rec.LoadFileArgs.Should().ContainSingle().Which.Should().Be(("C:/next.mp4", 12.5));
        rec.LoadedTrackId.Should().Be(track.Id);
        rec.Calls.Should().NotContain("ReadPosition");
        if (exitingGap)
            rec.Calls.IndexOf("LoadFile").Should().BeLessThan(rec.Calls.IndexOf("ResumePlayback"));
    }

    [Fact]
    public void GapExit_NativeSeeking_AllowsSameClipReentrySeek()
    {
        var track = CreateTrack(Guid.NewGuid());
        var rec = new Recorder
        {
            LoadedTrackId = track.Id, NativeSeeking = true,
            GapExit = GapExitActionType.ResumePlayback
        };
        var coordinator = new ContinueOnTrackCoordinator(CreateService(), CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(track, 12.5), 12.5).Should().Be(SyncRequestResult.Complete);

        rec.SeekTargets.Should().Equal(12.5);
        rec.Calls.Should().Contain("ResumePlayback");
        rec.Calls.Should().NotContain("ReadPosition");
    }

    // ---- (a) Gap 終了（ResumePlayback）分岐 ----

    [Fact]
    public void GapExit_ResumePlayback_CallsSeekThenPauseThenOsd_InOrder_AndReturns()
    {
        var track = CreateTrack(Guid.NewGuid());
        var rec = new Recorder { GapExit = GapExitActionType.ResumePlayback, LoadedTrackId = track.Id };
        var coordinator = new ContinueOnTrackCoordinator(CreateService(), CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(track, mediaPos: 42.0), ltcSeconds: 42.0);

        rec.Calls.Should().Equal(
            "GetLoadedTrackId",
            "SeekTo",
            "DecideGapExit",
            "ClearGapFreezeFrame",
            "ResumePlayback",
            "ApplyPauseState(False)",
            "UpdateCurrentTrackLabel");
        rec.SeekTargets.Should().ContainSingle().Which.Should().Be(42.0);
        // 他分岐（トラック判定・LoadFile・ReadPosition）には進まない
        rec.Calls.Should().NotContain(new[] { "LoadFile", "ReadPosition" });
    }

    [Fact]
    public void GapExit_PreexistingManualPause_SeeksButDoesNotResume()
    {
        var track = CreateTrack(Guid.NewGuid());
        var rec = new Recorder { LoadedTrackId = track.Id };
        var coordinator = new ContinueOnTrackCoordinator(
            CreateService(),
            CreateLogState(),
            new ContinueOnTrackEffects(
                PeekGapExit: () => new GapExitAction(GapExitActionType.ResumePlayback, ShouldResumePlayback: false),
                IsPlaybackPaused: () => true,
                ClearGapFreezeFrame: () => { },
                DecideGapExit: () =>
                {
                    rec.Calls.Add("DecideGapExit");
                    return new GapExitAction(GapExitActionType.ResumePlayback, ShouldResumePlayback: false);
                },
                SeekTo: target => { rec.Calls.Add("SeekTo"); rec.SeekTargets.Add(target); return true; },
                ResumePlayback: () => rec.Calls.Add("ResumePlayback"),
                ApplyPauseState: paused => rec.Calls.Add($"ApplyPauseState({paused})"),
                UpdateCurrentTrackLabel: () => rec.Calls.Add("UpdateCurrentTrackLabel"),
                GetLoadedTrackId: () => rec.LoadedTrackId,
                SetLoadedTrackId: id => rec.LoadedTrackId = id,
                LoadFile: (_, _) => true,
                GetTotalRenderedFrames: () => 0,
                ReadPosition: () => new SyncPositionRead(true, 0),
                BuildPlaybackState: SeekYieldingState));

        coordinator.Handle(OnTrack(track, mediaPos: 42.0), ltcSeconds: 42.0);

        rec.Calls.Should().Equal(
            "SeekTo",
            "DecideGapExit",
            "UpdateCurrentTrackLabel");
        rec.SeekTargets.Should().ContainSingle().Which.Should().Be(42.0);
    }

    // ---- (b) SwitchTrack 分岐 ----

    private static TimecodeSyncService CreateServiceWithLead(TrackSwitchLoadLead lead) =>
        new(new SyncDecisionEngine(new SyncDecisionOptions(), new SeekLatencyCompensator(enabled: false)),
            new TimecodeSyncSeekState(), null, new SeekLatencyCompensator(enabled: false), lead);

    // 先回りなしの切替を 1 回測ったことにする（最初の評価で loadSeconds のずれが残った）。
    private static void RecordSwitchLoad(TrackSwitchLoadLead lead, Guid track, double loadSeconds)
    {
        lead.MarkLoadSent(track, 0.0);
        lead.ObserveFirstResidual(track, loadSeconds);
    }

    [Fact]
    public void SwitchTrack_LoadsAheadByLearnedSwitchLoadTime()
    {
        var loadedId = Guid.NewGuid();
        var newTrack = CreateTrack(Guid.NewGuid(), path: "C:/next.mp4");
        var lead = new TrackSwitchLoadLead(enabled: true);
        RecordSwitchLoad(lead, newTrack.Id, 2.5);   // 1 回目（冷えた状態）は使わない
        RecordSwitchLoad(lead, newTrack.Id, 0.25);
        var service = CreateServiceWithLead(lead);
        var rec = new Recorder
        {
            LoadedTrackId = loadedId,   // != newTrack.Id → SwitchTrack
            LoadFileResult = true,
            TotalRenderedFrames = 7,
        };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(newTrack, mediaPos: 12.5), ltcSeconds: 12.5);

        rec.LoadFileArgs.Should().ContainSingle();
        rec.LoadFileArgs[0].path.Should().Be("C:/next.mp4");
        rec.LoadFileArgs[0].start.Should().BeApproximately(12.75, 1e-9);
        rec.SetLoadedTrackIds.Should().ContainSingle().Which.Should().Be(newTrack.Id);
        rec.Calls.Should().NotContain("ReadPosition");
    }

    [Fact]
    public void SwitchTrack_WithLeadDisabled_LoadsAtMediaPos()
    {
        var lead = new TrackSwitchLoadLead(enabled: false);
        var newTrack = CreateTrack(Guid.NewGuid(), path: "C:/next.mp4");
        RecordSwitchLoad(lead, newTrack.Id, 0.5);
        RecordSwitchLoad(lead, newTrack.Id, 0.5);
        var service = CreateServiceWithLead(lead);
        var rec = new Recorder
        {
            LoadedTrackId = Guid.NewGuid(),   // != newTrack.Id → SwitchTrack
            LoadFileResult = true,
        };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(newTrack, mediaPos: 12.5), ltcSeconds: 12.5);

        rec.LoadFileArgs.Should().ContainSingle().Which.Should().Be(("C:/next.mp4", 12.5));
    }

    [Fact]
    public void SwitchTrack_SeekCompensatorEnabled_DoesNotShiftSwitchLoad()
    {
        // シークの先行補償（TCS_SEEK_LATENCY_COMPENSATION=on）は切替の読み込みには使わない。
        var service = CreateService();   // シーク補償が有効な共有インスタンス、切替の学習は空
        var newTrack = CreateTrack(Guid.NewGuid(), path: "C:/next.mp4");
        var compensator = service.LatencyCompensator;
        compensator.SelectTrack(newTrack.Id);
        compensator.MarkLoadSent(1_000);
        compensator.ObserveFrameReady(1_000 + (long)(0.25 * System.Diagnostics.Stopwatch.Frequency), generation: 1, sourceSequence: 1);
        var rec = new Recorder { LoadedTrackId = Guid.NewGuid(), LoadFileResult = true };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(newTrack, mediaPos: 12.5), ltcSeconds: 12.5);

        rec.LoadFileArgs.Should().ContainSingle().Which.Should().Be(("C:/next.mp4", 12.5));
    }

    [Fact]
    public void SwitchTrack_LearnsFromTheResidualAfterTheLoad_AndLeadsTheNextSwitch()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        var service = CreateServiceWithLead(lead);
        var newTrack = CreateTrack(Guid.NewGuid(), path: "C:/next.mp4");
        var rec = new Recorder { LoadedTrackId = Guid.NewGuid(), LoadFileResult = true };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        // 読み込み → 安定後の最初の評価で、素材位置 − 再生位置 = residual が残る。
        void SwitchAndSettle(double mediaPos, double residual)
        {
            rec.LoadedTrackId = Guid.NewGuid();                        // 別のトラックから切り替える
            coordinator.Handle(OnTrack(newTrack, mediaPos), mediaPos);
            double loaded = rec.LoadFileArgs[^1].start;
            rec.TotalRenderedFrames += 5;
            rec.TimePos = (0, loaded + 0.1);
            coordinator.Handle(OnTrack(newTrack, loaded + 0.1 + residual), loaded + 0.1 + residual);
        }

        SwitchAndSettle(12.5, residual: 0.9);    // 冷えた 1 回目（学習に使わない）
        rec.LoadFileArgs[^1].start.Should().Be(12.5);
        lead.LeadForTrack(newTrack.Id).Should().Be(0.0);

        SwitchAndSettle(30.0, residual: 0.25);   // 先回りなしで 0.25 秒残った
        rec.LoadFileArgs[^1].start.Should().Be(30.0);
        lead.LeadForTrack(newTrack.Id).Should().BeApproximately(0.25, 1e-9);

        SwitchAndSettle(40.0, residual: -0.05);  // 0.25 秒先回りして 0.05 秒先に出た → 必要だったのは 0.20
        rec.LoadFileArgs[^1].start.Should().BeApproximately(40.25, 1e-9);
        lead.LeadForTrack(newTrack.Id).Should().BeApproximately((0.25 + 0.20) / 2, 1e-9);
    }

    [Fact]
    public void SwitchTrack_GapExitKeepingPause_DoesNotLeadNorMeasure()
    {
        var lead = new TrackSwitchLoadLead(enabled: true);
        var newTrack = CreateTrack(Guid.NewGuid(), path: "C:/next.mp4");
        RecordSwitchLoad(lead, newTrack.Id, 1.0);
        RecordSwitchLoad(lead, newTrack.Id, 0.3);
        var service = CreateServiceWithLead(lead);
        var rec = new Recorder
        {
            LoadedTrackId = Guid.NewGuid(),
            LoadFileResult = true,
        };
        var effects = rec.Build() with
        {
            PeekGapExit = () => new GapExitAction(GapExitActionType.ResumePlayback, ShouldResumePlayback: false),
            DecideGapExit = () => new GapExitAction(GapExitActionType.ResumePlayback, ShouldResumePlayback: false),
        };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), effects);

        coordinator.Handle(OnTrack(newTrack, mediaPos: 12.5), ltcSeconds: 12.5);

        rec.LoadFileArgs.Should().ContainSingle().Which.Should().Be(("C:/next.mp4", 12.5));
        // 測定していないので、次の評価のずれで学習値は変わらない。
        lead.ObserveFirstResidual(newTrack.Id, 2.0);
        lead.LeadForTrack(newTrack.Id).Should().BeApproximately(0.3, 1e-6);
    }

    [Fact]
    public void SwitchTrack_LeadPastLastFrame_LoadsAtMediaPos()
    {
        // 残り 0.2 秒のクリップへ 0.5 秒先回りすると出口の外になる → 先回りしない。
        var lead = new TrackSwitchLoadLead(enabled: true);
        var newTrack = CreateTrack(Guid.NewGuid(), path: "C:/next.mp4") with { MediaOut = TimeSpan.FromSeconds(12.7) };
        RecordSwitchLoad(lead, newTrack.Id, 0.5);
        RecordSwitchLoad(lead, newTrack.Id, 0.5);
        var service = CreateServiceWithLead(lead);
        var rec = new Recorder { LoadedTrackId = Guid.NewGuid(), LoadFileResult = true };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(newTrack, mediaPos: 12.5), ltcSeconds: 12.5);

        rec.LoadFileArgs.Should().ContainSingle().Which.Should().Be(("C:/next.mp4", 12.5));
    }

    [Fact]
    public void SwitchTrack_OnLoadFileSuccess_UpdatesLoadedTrackId_AndBeginsFileLoad()
    {
        var loadedId = Guid.NewGuid();
        var newTrack = CreateTrack(Guid.NewGuid(), path: "C:/next.mp4");
        var service = CreateService();
        var rec = new Recorder
        {
            LoadedTrackId = loadedId,   // != newTrack.Id → SwitchTrack
            LoadFileResult = true,
            TotalRenderedFrames = 7,
        };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(newTrack, mediaPos: 12.5), ltcSeconds: 12.5);

        rec.LoadFileArgs.Should().ContainSingle().Which.Should().Be(("C:/next.mp4", 12.5));
        rec.SetLoadedTrackIds.Should().ContainSingle().Which.Should().Be(newTrack.Id);
        rec.Calls.Should().ContainSingle(call => call == "UpdateCurrentTrackLabel");
        // BeginFileLoad が呼ばれると以後のシークが抑止される
        service.ShouldSuppressSeek(playbackSeconds: 12.5, toleranceSeconds: 0.2).Should().BeTrue();
        // 同一トラック分岐へは進まない
        rec.Calls.Should().NotContain("ReadPosition");
    }

    [Fact]
    public void SwitchTrack_OnLoadFileFailure_DoesNotUpdateLoadedTrackId_NorBeginFileLoad()
    {
        var loadedId = Guid.NewGuid();
        var newTrack = CreateTrack(Guid.NewGuid());
        var service = CreateService();
        var rec = new Recorder
        {
            LoadedTrackId = loadedId,
            LoadFileResult = false,
        };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(newTrack, mediaPos: 12.5), ltcSeconds: 12.5);

        rec.Calls.Should().Contain("LoadFile");
        rec.Calls.Should().NotContain("UpdateCurrentTrackLabel");
        rec.SetLoadedTrackIds.Should().BeEmpty();
        // BeginFileLoad は呼ばれていない（抑止フラグが立たない）
        service.ShouldSuppressSeek(playbackSeconds: 12.5, toleranceSeconds: 0.2).Should().BeFalse();
    }

    // ---- (c) 同一トラック同期分岐 ----

    [Fact]
    public void SameTrack_WhenTimePosReadFails_DoesNothingFurther()
    {
        var id = Guid.NewGuid();
        var track = CreateTrack(id);
        var rec = new Recorder
        {
            LoadedTrackId = id,                 // == track.Id → ContinueCurrentTrack
            TimePos = (rc: 1, playbackSeconds: 0.0),
        };
        var coordinator = new ContinueOnTrackCoordinator(CreateService(), CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(track, mediaPos: 100.0), ltcSeconds: 100.0);

        rec.Calls.Should().Contain("ReadPosition");
        rec.Calls.Should().NotContain(new[] { "BuildPlaybackState", "SeekTo" });
    }

    [Fact]
    public void SameTrack_WhenFileLoadNotStable_DoesNotBuildStateNorSeek()
    {
        var id = Guid.NewGuid();
        var track = CreateTrack(id);
        var service = CreateService();
        // ロード中かつ進捗未達 → TryMarkFileLoaded が false
        service.BeginFileLoad(startPositionSeconds: 5.0, renderedFrameCount: 100);
        var rec = new Recorder
        {
            LoadedTrackId = id,
            TimePos = (rc: 0, playbackSeconds: 5.0),  // 進捗なし
            TotalRenderedFrames = 100,
        };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(track, mediaPos: 100.0), ltcSeconds: 100.0);

        rec.Calls.Should().NotContain(new[] { "BuildPlaybackState", "SeekTo" });
    }

    [Fact]
    public void SameTrack_WhenLoadIsNotPending_CanSeekFromFirstHalfSecond()
    {
        var id = Guid.NewGuid();
        var track = CreateTrack(id);
        var rec = new Recorder
        {
            LoadedTrackId = id,
            TimePos = (rc: 0, playbackSeconds: 0.4),   // < 0.5
        };
        var coordinator = new ContinueOnTrackCoordinator(CreateService(), CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(track, mediaPos: 100.0), ltcSeconds: 100.0);

        rec.SeekTargets.Should().ContainSingle().Which.Should().Be(100.0);
    }

    [Fact]
    public void SameTrack_WhenDecisionIsNotSeek_DoesNotSeek()
    {
        var id = Guid.NewGuid();
        var track = CreateTrack(id);
        var rec = new Recorder
        {
            LoadedTrackId = id,
            TimePos = (rc: 0, playbackSeconds: 5.0),
            BuildState = NoSeekState,   // SyncEnabled=false → decision != Seek
        };
        var coordinator = new ContinueOnTrackCoordinator(CreateService(), CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(track, mediaPos: 100.0), ltcSeconds: 100.0);

        rec.Calls.Should().Contain("BuildPlaybackState");
        rec.SeekTargets.Should().BeEmpty();
    }

    [Fact]
    public void SameTrack_WhenSeekSucceeds_SeeksToMediaPos_AndReportsSeekSent()
    {
        var id = Guid.NewGuid();
        var track = CreateTrack(id);
        var service = CreateService();
        var rec = new Recorder
        {
            LoadedTrackId = id,
            TimePos = (rc: 0, playbackSeconds: 5.0),
            SeekResult = true,
        };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(track, mediaPos: 100.0), ltcSeconds: 100.0);

        rec.SeekTargets.Should().ContainSingle().Which.Should().Be(100.0);
        service.SeekState.HasPendingSeek.Should().BeTrue();
        service.SeekState.TargetSeconds.Should().Be(100.0);
    }

    [Fact]
    public void SameTrack_WhenSeekFails_DoesNotReportSeekSent()
    {
        var id = Guid.NewGuid();
        var track = CreateTrack(id);
        var service = CreateService();
        var rec = new Recorder
        {
            LoadedTrackId = id,
            TimePos = (rc: 0, playbackSeconds: 5.0),
            SeekResult = false,
        };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(track, mediaPos: 100.0), ltcSeconds: 100.0);

        rec.SeekTargets.Should().ContainSingle().Which.Should().Be(100.0);
        service.SeekState.HasPendingSeek.Should().BeFalse();
    }

    // ---- D37-b2: 着地直後は速度補正に任せずシークで詰める ----

    private static (SyncDecisionEngine Engine, TimecodeSyncService Service, ManualTimeProvider Clock)
        CreateServiceWithSimulatedEngineClock()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 18, 0, 0, 0, TimeSpan.Zero));
        var engine = new SyncDecisionEngine(new SyncDecisionOptions(), null,
            () => clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0);
        var service = new TimecodeSyncService(engine, new TimecodeSyncSeekState(), clock);
        return (engine, service, clock);
    }

    [Fact]
    public void GapExitLanding_SubsequentDeficit_SeeksInsteadOfRateCatchUp()
    {
        (_, TimecodeSyncService service, ManualTimeProvider clock) = CreateServiceWithSimulatedEngineClock();
        var track = CreateTrack(Guid.NewGuid());
        var rec = new Recorder { LoadedTrackId = track.Id, TimePos = (0, 10.0) };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        // 定常で 1 サンプル（許容内）を消費し、起動直後の例外を使い切る。
        coordinator.Handle(OnTrack(track, 10.0), 10.0);
        // ギャップ出口: mediaPos 10.0 へ直接シークしてギャップを抜ける。
        rec.GapExit = GapExitActionType.ResumePlayback;
        coordinator.Handle(OnTrack(track, 10.0), 10.0);
        rec.SeekTargets.Should().Equal(10.0);

        // 出口直後の不足 0.7 秒（着地窓の中、0.5× シーク所要 1.0 秒を超える）→ シークで着地する。
        rec.GapExit = GapExitActionType.None;
        rec.TimePos = (0, 9.5);
        for (int i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            coordinator.Handle(OnTrack(track, 10.2), 10.2);
        }

        rec.SeekTargets.Should().Equal(10.0, 10.2);
    }

    [Fact]
    public void GapExitLanding_WithLearnedSeekCost_DoesNotLookAhead()
    {
        (_, TimecodeSyncService service, ManualTimeProvider clock) = CreateServiceWithSimulatedEngineClock();
        // D37-e: 学習値 2.0 があっても、ギャップ出口のシークは先行しない（対象は追従開始だけ）。
        service.SeekState.BeginSeek(1.0, clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromSeconds(2.0));
        service.SeekState.ShouldSuppressSeek(1.0, 0.24, clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        service.SeekState.ShouldSuppressSeek(1.0, 0.24, clock.GetUtcNow().UtcDateTime);
        service.SeekState.LearnedSeekDurationSeconds.Should().BeApproximately(2.0, 1e-6);
        clock.Advance(TimeSpan.FromMilliseconds(600));

        var track = CreateTrack(Guid.NewGuid());
        var rec = new Recorder { LoadedTrackId = track.Id, TimePos = (0, 10.0) };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        // 定常で 1 サンプル（許容内）を消費し、起動直後の例外を使い切る。
        coordinator.Handle(OnTrack(track, 10.0), 10.0);
        // ギャップ出口: mediaPos 10.0 へ直接シークしてギャップを抜ける。
        rec.GapExit = GapExitActionType.ResumePlayback;
        coordinator.Handle(OnTrack(track, 10.0), 10.0);
        rec.SeekTargets.Should().Equal(10.0);

        // 出口直後の不足 1.2 秒（> 0.5 × 学習値 2.0、< 学習値）→ シーク。行き先は LTC のまま。
        rec.GapExit = GapExitActionType.None;
        rec.TimePos = (0, 9.0);
        for (int i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            coordinator.Handle(OnTrack(track, 10.2), 10.2);
        }

        rec.SeekTargets.Should().Equal(10.0, 10.2);
    }

    [Fact]
    public void GapExitLanding_SmallDeficitBelowHalfSeekCost_UsesRateCatchUp()
    {
        (_, TimecodeSyncService service, ManualTimeProvider clock) = CreateServiceWithSimulatedEngineClock();
        var track = CreateTrack(Guid.NewGuid());
        var rec = new Recorder { LoadedTrackId = track.Id, TimePos = (0, 10.0) };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        // 定常で 1 サンプル（許容内）を消費し、起動直後の例外を使い切る。
        coordinator.Handle(OnTrack(track, 10.0), 10.0);
        // ギャップ出口: mediaPos 10.0 へ直接シークしてギャップを抜ける。
        rec.GapExit = GapExitActionType.ResumePlayback;
        coordinator.Handle(OnTrack(track, 10.0), 10.0);
        rec.SeekTargets.Should().Equal(10.0);

        // 出口直後の不足 0.3 秒（0.5× 1.0 秒以下）→ シークは誤差を増やすだけなので速度補正に任せる。
        rec.GapExit = GapExitActionType.None;
        rec.TimePos = (0, 9.9);
        for (int i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            coordinator.Handle(OnTrack(track, 10.2), 10.2);
        }

        rec.SeekTargets.Should().Equal(10.0);
    }

    [Fact]
    public void SteadyDeficit_WithinSeekCost_UsesRateCatchUp()
    {
        (_, TimecodeSyncService service, ManualTimeProvider clock) = CreateServiceWithSimulatedEngineClock();
        var track = CreateTrack(Guid.NewGuid());
        var rec = new Recorder { LoadedTrackId = track.Id, TimePos = (0, 10.0) };
        var coordinator = new ContinueOnTrackCoordinator(service, CreateLogState(), rec.Build());

        // 定常で 1 サンプル（許容内）を消費し、起動直後の例外を使い切る。
        coordinator.Handle(OnTrack(track, 10.0), 10.0);
        // 定常中の同じ大きさの不足 0.5 秒 → シークを出さず速度補正に任せる。
        rec.TimePos = (0, 9.7);
        for (int i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            coordinator.Handle(OnTrack(track, 10.2), 10.2);
        }

        rec.SeekTargets.Should().BeEmpty();
    }
}
