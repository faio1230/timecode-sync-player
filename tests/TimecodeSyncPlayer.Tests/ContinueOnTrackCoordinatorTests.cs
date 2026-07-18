using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ContinueOnTrackCoordinatorTests
{
    private static TimecodeSyncService CreateService() =>
        new(new SyncDecisionEngine(), new TimecodeSyncSeekState());

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
        public Guid? LoadedTrackId;
        public long TotalRenderedFrames;
        public (int rc, double playbackSeconds) TimePos = (0, 1.0);
        public Func<double, SyncPlaybackState> BuildState = SeekYieldingState;

        public ContinueOnTrackEffects Build() => new(
            DecideGapExit: () => { Calls.Add("DecideGapExit"); return new GapExitAction(GapExit); },
            SeekTo: target => { Calls.Add("SeekTo"); SeekTargets.Add(target); return SeekResult; },
            ResumeMpvPause: () => Calls.Add("ResumeMpvPause"),
            ApplyPauseState: paused => Calls.Add($"ApplyPauseState({paused})"),
            ShowOsdBar: () => Calls.Add("ShowOsdBar"),
            UpdateCurrentTrackLabel: () => Calls.Add("UpdateCurrentTrackLabel"),
            GetLoadedTrackId: () => { Calls.Add("GetLoadedTrackId"); return LoadedTrackId; },
            SetLoadedTrackId: id => { Calls.Add("SetLoadedTrackId"); SetLoadedTrackIds.Add(id); LoadedTrackId = id; },
            LoadFile: (path, start) => { Calls.Add("LoadFile"); LoadFileArgs.Add((path, start)); return LoadFileResult; },
            GetTotalRenderedFrames: () => { Calls.Add("GetTotalRenderedFrames"); return TotalRenderedFrames; },
            GetTimePos: () => { Calls.Add("GetTimePos"); return TimePos; },
            BuildPlaybackState: ps => { Calls.Add("BuildPlaybackState"); return BuildState(ps); });
    }

    private static TimelineQueryResult OnTrack(PlaylistTrack track, double mediaPos) =>
        new(TimelineQueryStatus.OnTrack, track, mediaPos, null);

    // ---- (a) Gap 終了（ResumePlayback）分岐 ----

    [Fact]
    public void GapExit_ResumePlayback_CallsSeekThenPauseThenOsd_InOrder_AndReturns()
    {
        var track = CreateTrack(Guid.NewGuid());
        var rec = new Recorder { GapExit = GapExitActionType.ResumePlayback };
        var coordinator = new ContinueOnTrackCoordinator(CreateService(), CreateLogState(), rec.Build());

        coordinator.Handle(OnTrack(track, mediaPos: 42.0), ltcSeconds: 42.0);

        rec.Calls.Should().Equal(
            "DecideGapExit",
            "SeekTo",
            "ResumeMpvPause",
            "ApplyPauseState(False)",
            "ShowOsdBar",
            "UpdateCurrentTrackLabel");
        rec.SeekTargets.Should().ContainSingle().Which.Should().Be(42.0);
        // 他分岐（トラック判定・LoadFile・GetTimePos）には進まない
        rec.Calls.Should().NotContain(new[] { "GetLoadedTrackId", "LoadFile", "GetTimePos" });
    }

    [Fact]
    public void GapExit_PreexistingManualPause_SeeksButDoesNotResume()
    {
        var track = CreateTrack(Guid.NewGuid());
        var rec = new Recorder();
        var coordinator = new ContinueOnTrackCoordinator(
            CreateService(),
            CreateLogState(),
            new ContinueOnTrackEffects(
                DecideGapExit: () =>
                {
                    rec.Calls.Add("DecideGapExit");
                    return new GapExitAction(GapExitActionType.ResumePlayback, ShouldResumePlayback: false);
                },
                SeekTo: target => { rec.Calls.Add("SeekTo"); rec.SeekTargets.Add(target); return true; },
                ResumeMpvPause: () => rec.Calls.Add("ResumeMpvPause"),
                ApplyPauseState: paused => rec.Calls.Add($"ApplyPauseState({paused})"),
                ShowOsdBar: () => rec.Calls.Add("ShowOsdBar"),
                UpdateCurrentTrackLabel: () => rec.Calls.Add("UpdateCurrentTrackLabel"),
                GetLoadedTrackId: () => rec.LoadedTrackId,
                SetLoadedTrackId: id => rec.LoadedTrackId = id,
                LoadFile: (_, _) => true,
                GetTotalRenderedFrames: () => 0,
                GetTimePos: () => (0, 0),
                BuildPlaybackState: SeekYieldingState));

        coordinator.Handle(OnTrack(track, mediaPos: 42.0), ltcSeconds: 42.0);

        rec.Calls.Should().Equal(
            "DecideGapExit",
            "SeekTo",
            "ShowOsdBar",
            "UpdateCurrentTrackLabel");
        rec.SeekTargets.Should().ContainSingle().Which.Should().Be(42.0);
    }

    // ---- (b) SwitchTrack 分岐 ----

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
        rec.Calls.Should().NotContain("GetTimePos");
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

        rec.Calls.Should().Contain("GetTimePos");
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
    public void SameTrack_WhenPlaybackBelowHalfSecond_DoesNotSeek()
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

        rec.Calls.Should().NotContain(new[] { "BuildPlaybackState", "SeekTo" });
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
}
