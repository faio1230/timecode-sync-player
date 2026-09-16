using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

// 共有静的 OutputTrace.Current を差し替えるテスト（gap.enter の記録確認）があるため、
// OutputTrace コレクションで並列実行を止める。
[Collection("OutputTrace")]
public class GapEnterCoordinatorTests
{
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

    /// <summary>全副作用デリゲート呼び出しを順序込みで記録するフェイク。</summary>
    private sealed class Recorder
    {
        public readonly List<string> Calls = new();
        public readonly List<double> SeekTargets = new();
        public readonly List<(string path, double target)> LoadArgs = new();
        public readonly List<Guid> SetLoadedTrackIds = new();

        public bool EndAdvanceTriggered = true;   // false へ落とされたか観測用（初期 true）
        public bool SeekResult = true;
        public (int rc, double duration) PlayerDuration = (0, 120.0);
        public bool PlayerReady = true;
        public GapLoadCommandResult LoadResult = new(PlaybackResult.Ok, PlaybackResult.Ok);
        public Guid? LoadedTrackId;
        public double Duration = 10.0;
        public double Fps = 25.0;
        public GapBehavior GapBehavior = GapBehavior.Freeze;

        public GapEnterEffects Build() => new(
            ResetEndAdvanceTriggered: () => { Calls.Add("ResetEndAdvanceTriggered"); EndAdvanceTriggered = false; },
            PauseForGap: () => Calls.Add("PauseForGap"),
            ApplyPauseState: paused => Calls.Add($"ApplyPauseState({paused})"),
            ClearGapFreezeFrame: () => Calls.Add("ClearGapFreezeFrame"),
            SeekTo: target => { Calls.Add("SeekTo"); SeekTargets.Add(target); return SeekResult; },
            GetPlayerDuration: () => { Calls.Add("GetPlayerDuration"); return PlayerDuration; },
            IsPlayerReady: () => { Calls.Add("IsPlayerReady"); return PlayerReady; },
            LoadPausedAt: (path, target) => { Calls.Add("LoadPausedAt"); LoadArgs.Add((path, target)); return LoadResult; },
            ResetPlayerStateForNewTrack: () => Calls.Add("ResetPlayerStateForNewTrack"),
            GetLoadedTrackId: () => LoadedTrackId,
            SetLoadedTrackId: id => { Calls.Add("SetLoadedTrackId"); SetLoadedTrackIds.Add(id); LoadedTrackId = id; },
            GetDuration: () => { Calls.Add("GetDuration"); return Duration; },
            SetDuration: d => { Calls.Add("SetDuration"); Duration = d; },
            GetFps: () => { Calls.Add("GetFps"); return Fps; },
            SetFps: f => { Calls.Add("SetFps"); Fps = f; },
            GetGapBehavior: () => GapBehavior,
            UpdateCurrentTrackLabel: () => Calls.Add("UpdateCurrentTrackLabel"));
    }

    private static (GapEnterCoordinator coord, GapFreezeHandler handler, Recorder rec) Build(
        Action<Recorder>? configure = null, GapPlayerMode mode = GapPlayerMode.Pause)
    {
        var handler = new GapFreezeHandler();
        var rec = new Recorder();
        configure?.Invoke(rec);
        var coord = new GapEnterCoordinator(handler, rec.Build(), mode);
        return (coord, handler, rec);
    }

    private static TimelineQueryResult GapWithPrevious(PlaylistTrack? previous) =>
        new(TimelineQueryStatus.Gap, null, 0.0, previous);

    // ---- EnterBlackGap ----

    [Fact]
    public void EnterBlackGap_PausesThenAppliesPause_AndResetsEndAdvance()
    {
        var (coord, _, rec) = Build();

        coord.EnterBlackGap();

        rec.Calls.Should().Equal(
            "ResetEndAdvanceTriggered",
            "PauseForGap",
            "ApplyPauseState(True)");
        rec.EndAdvanceTriggered.Should().BeFalse();
    }

    [Fact]
    public void EnterBlackGap_ComposeBlack_DoesNotPausePlayer_ButAppliesGapState()
    {
        var (coord, _, rec) = Build(mode: GapPlayerMode.ComposeBlack);

        coord.EnterBlackGap();

        // C1(a): compose-black は PauseForGap を呼ばない。UI のギャップ状態は現状どおり。
        rec.Calls.Should().Equal(
            "ResetEndAdvanceTriggered",
            "ApplyPauseState(True)");
        rec.EndAdvanceTriggered.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, "mode=pause")]
    [InlineData(1, "mode=compose-black")]
    public void EnterBlackGap_RecordsGapEnterTraceEventWithMode(int modeValue, string expected)
    {
        GapPlayerMode mode = (GapPlayerMode)modeValue;
        string dir = Path.Combine(Path.GetTempPath(), "tcs-gap-trace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var trace = new OutputTrace(dir, capacity: 100) { OriginQpc = Stopwatch.GetTimestamp() };
            OutputTrace.Current = trace;
            try
            {
                var (coord, _, _) = Build(mode: mode);
                coord.EnterBlackGap();
            }
            finally
            {
                OutputTrace.Current = OutputTrace.Disabled;
            }

            trace.Save(
                new OutputTraceRunSummary("completed", false, false, 16, 16, 3, 3, "test", 0, 0, 0, null, false, null),
                new LatestPool(3),
                null);
            var events = File.ReadAllLines(Path.Combine(dir, "events.jsonl"))
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToList();
            events.Where(e => e.GetProperty("stage").GetString() == "gap.enter")
                .Should().Contain(e => e.GetProperty("detail").GetString() == expected);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 一時ディレクトリ */ }
        }
    }

    // ---- EnterForceBlack ----

    [Fact]
    public void EnterForceBlack_ClearsGapFreezeFrameBeforePause_AndResetsEndAdvance()
    {
        var (coord, _, rec) = Build();

        coord.EnterForceBlack();

        rec.Calls.Should().Equal(
            "ResetEndAdvanceTriggered",
            "ClearGapFreezeFrame",
            "PauseForGap",
            "ApplyPauseState(True)");
        rec.EndAdvanceTriggered.Should().BeFalse();
    }

    // ---- StartGapFreezeCaptureForCurrentTrack ----

    [Fact]
    public void StartGapFreeze_TargetLeqZero_OnlyFreezeComplete_NoSeek()
    {
        var prev = CreateTrack(Guid.NewGuid());
        var loadedId = Guid.NewGuid();
        var (coord, handler, rec) = Build(r => r.LoadedTrackId = loadedId);
        var action = new GapEnterAction(GapEnterActionType.SeekToFinalFrame, prev.Id, TargetSeconds: 0, DurationSeconds: 50, Fps: 30);

        coord.StartGapFreezeCaptureForCurrentTrack(GapWithPrevious(prev), action);

        rec.Calls.Should().Equal(
            "ResetEndAdvanceTriggered",
            "PauseForGap",
            "ApplyPauseState(True)");
        rec.SeekTargets.Should().BeEmpty();
        rec.Calls.Should().NotContain(new[] { "GetDuration", "GetFps" });
        handler.CurrentState.Should().Be(GapState.FreezeComplete);
        // A missing duration did not produce or copy a final image.
        handler.CachedTrackId.Should().BeNull();
    }

    [Fact]
    public void StartGapFreeze_SeekSuccess_EntersFreezeCapture_WithPreviousTrackId()
    {
        var prev = CreateTrack(Guid.NewGuid(), path: "C:/prev.mp4");
        var (coord, handler, rec) = Build(r => { r.LoadedTrackId = Guid.NewGuid(); r.SeekResult = true; });
        var action = new GapEnterAction(GapEnterActionType.SeekToFinalFrame, prev.Id, TargetSeconds: 49.9, DurationSeconds: 50, Fps: 30);

        coord.StartGapFreezeCaptureForCurrentTrack(GapWithPrevious(prev), action);

        rec.SeekTargets.Should().ContainSingle().Which.Should().Be(49.9);
        rec.Calls.Should().NotContain("GetDuration");
        rec.Calls.Should().Contain("GetFps");
        handler.CurrentState.Should().Be(GapState.EnteringFreeze);
        handler.PendingTrackId.Should().Be(prev.Id);
        handler.PendingTargetSeconds.Should().Be(49.9);
        handler.PendingPath.Should().Be("C:/prev.mp4");
    }

    [Fact]
    public void StartGapFreeze_SeekFailure_ForcesFreezeComplete()
    {
        var prev = CreateTrack(Guid.NewGuid());
        var (coord, handler, rec) = Build(r => { r.LoadedTrackId = Guid.NewGuid(); r.SeekResult = false; });
        var action = new GapEnterAction(GapEnterActionType.SeekToFinalFrame, prev.Id, TargetSeconds: 49.9, DurationSeconds: 50, Fps: 30);

        coord.StartGapFreezeCaptureForCurrentTrack(GapWithPrevious(prev), action);

        rec.SeekTargets.Should().ContainSingle();
        handler.CurrentState.Should().Be(GapState.FreezeComplete);
    }

    // ---- EnterNoTracksFreeze ----

    [Fact]
    public void EnterNoTracksFreeze_DurationOkAndSeekSuccess_EntersFreezeCapture_DoesNotResetEndAdvance()
    {
        var loadedId = Guid.NewGuid();
        var (coord, handler, rec) = Build(r =>
        {
            r.LoadedTrackId = loadedId;
            r.PlayerDuration = (0, 100.0);
            r.Fps = 25.0;
            r.SeekResult = true;
        });

        coord.EnterNoTracksFreeze();

        // target = 100 - 1/25 = 99.96
        rec.SeekTargets.Should().ContainSingle().Which.Should().BeApproximately(99.96, 1e-9);
        handler.CurrentState.Should().Be(GapState.EnteringFreeze);
        handler.PendingTrackId.Should().Be(loadedId);
        // EnterNoTracksFreeze は _endAdvanceTriggered を触らない
        rec.Calls.Should().NotContain("ResetEndAdvanceTriggered");
        rec.EndAdvanceTriggered.Should().BeTrue();
    }

    [Fact]
    public void EnterNoTracksFreeze_DurationUnavailable_ForcesBlackState()
    {
        var (coord, handler, rec) = Build(r => r.PlayerDuration = (rc: 1, duration: 0.0));

        coord.EnterNoTracksFreeze();

        handler.CurrentState.Should().Be(GapState.ForceBlack);
        rec.SeekTargets.Should().BeEmpty();
    }

    [Fact]
    public void EnterNoTracksFreeze_SeekFailure_ForcesBlackState()
    {
        var (coord, handler, rec) = Build(r => { r.PlayerDuration = (0, 100.0); r.SeekResult = false; });

        coord.EnterNoTracksFreeze();

        rec.SeekTargets.Should().ContainSingle();
        handler.CurrentState.Should().Be(GapState.ForceBlack);
    }

    // ---- LoadPreviousTrackFinalFrameForGapFreeze ----

    [Fact]
    public void LoadPreviousTrack_PlayerNotReady_ReturnsEarly_AfterResettingEndAdvance()
    {
        var prev = CreateTrack(Guid.NewGuid());
        var (coord, handler, rec) = Build(r => r.PlayerReady = false);

        coord.LoadPreviousTrackFinalFrameForGapFreeze(prev, target: 49.9, duration: 50, fps: 30);

        rec.Calls.Should().Equal("ResetEndAdvanceTriggered", "IsPlayerReady");
        rec.EndAdvanceTriggered.Should().BeFalse();
        rec.SetLoadedTrackIds.Should().BeEmpty();
        handler.CurrentState.Should().Be(GapState.Inactive);
    }

    [Fact]
    public void LoadPreviousTrack_LoadFailure_ForcesFreezeComplete_DoesNotChangeLoadedTrackId()
    {
        var prev = CreateTrack(Guid.NewGuid());
        var existing = Guid.NewGuid();
        var (coord, handler, rec) = Build(r =>
        {
            r.LoadedTrackId = existing;
            r.LoadResult = new GapLoadCommandResult(PlaybackResult.Fail("load failed"), PlaybackResult.Ok);
        });

        coord.LoadPreviousTrackFinalFrameForGapFreeze(prev, target: 49.9, duration: 50, fps: 30);

        rec.LoadArgs.Should().ContainSingle().Which.Should().Be((prev.FilePath, 49.9));
        rec.SetLoadedTrackIds.Should().BeEmpty();
        rec.LoadedTrackId.Should().Be(existing);
        handler.CurrentState.Should().Be(GapState.FreezeComplete);
        rec.Calls.Should().NotContain(new[] { "SetDuration", "SetFps", "ResetPlayerStateForNewTrack" });
    }

    [Fact]
    public void LoadPreviousTrack_LoadSuccess_UpdatesLoadedTrackId_DurationFps_AndEntersFreezeWithReload()
    {
        var prev = CreateTrack(Guid.NewGuid(), path: "C:/prev.mp4");
        var (coord, handler, rec) = Build(r =>
        {
            r.LoadedTrackId = Guid.NewGuid();
            r.LoadResult = new GapLoadCommandResult(PlaybackResult.Ok, PlaybackResult.Ok);
        });

        coord.LoadPreviousTrackFinalFrameForGapFreeze(prev, target: 49.9, duration: 50.0, fps: 24.0);

        rec.SetLoadedTrackIds.Should().ContainSingle().Which.Should().Be(prev.Id);
        rec.LoadedTrackId.Should().Be(prev.Id);
        rec.Duration.Should().Be(50.0);
        rec.Fps.Should().Be(24.0);
        rec.EndAdvanceTriggered.Should().BeFalse();
        handler.CurrentState.Should().Be(GapState.EnteringFreeze);
        handler.PendingTrackId.Should().Be(prev.Id);
        // 実行順序: ロード成功後 pause→reset→duration/fps 更新
        rec.Calls.Should().ContainInOrder(
            "SetLoadedTrackId", "ApplyPauseState(True)", "ResetPlayerStateForNewTrack", "SetDuration", "SetFps");
    }

    // ---- HandleNoTracks ----

    [Fact]
    public void HandleNoTracks_FreezeWithLoadedTrack_EntersNoTracksFreeze_ThenUpdatesLabel()
    {
        var loadedId = Guid.NewGuid();
        var (coord, handler, rec) = Build(r =>
        {
            r.GapBehavior = GapBehavior.Freeze;
            r.LoadedTrackId = loadedId;
            r.PlayerDuration = (0, 100.0);
            r.SeekResult = true;
        });

        coord.HandleNoTracks();

        // NoTracks freeze パスを通っている
        rec.Calls.Should().Contain("GetPlayerDuration");
        handler.CurrentState.Should().Be(GapState.EnteringFreeze);
        rec.Calls.Last().Should().Be("UpdateCurrentTrackLabel");
    }

    [Fact]
    public void HandleNoTracks_BlackBehavior_ForcesBlack_ThenUpdatesLabel()
    {
        var (coord, handler, rec) = Build(r =>
        {
            r.GapBehavior = GapBehavior.Black;
            r.LoadedTrackId = Guid.NewGuid();
        });

        coord.HandleNoTracks();

        // DecideNoTracksEnter が ForceBlack を返し EnterForceBlack が呼ばれる
        rec.Calls.Should().Contain("ClearGapFreezeFrame");
        handler.CurrentState.Should().Be(GapState.ForceBlack);
        rec.Calls.Last().Should().Be("UpdateCurrentTrackLabel");
    }
}
