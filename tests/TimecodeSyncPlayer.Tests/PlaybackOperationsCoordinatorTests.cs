using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public class PlaybackOperationsCoordinatorTests
{
    private sealed class Recorder
    {
        public readonly List<string> Calls = new();
        public readonly List<(string Path, double? Start, bool Paused)> Loads = new();
        public readonly List<double> Seeks = new();
        public readonly List<bool> SetPausedCalls = new();
        public readonly List<double> BeginSyncFileLoads = new();
        public int StopCalls;
        public bool IsPlayerReady = true;
        public bool HasTimelinePanel = true;
        public PlaybackResult LoadResult = PlaybackResult.Ok;
        public PlaybackResult SeekResult = PlaybackResult.Ok;
        public Exception? SeekException;

        public PlaybackOperationsEffects Build() => new(
            IsPlayerReady: () => { Calls.Add("IsPlayerReady"); return IsPlayerReady; },
            Load: (path, start, paused) =>
            {
                Calls.Add($"Load({path},{start?.ToString() ?? "null"},{paused})");
                Loads.Add((path, start, paused));
                return LoadResult;
            },
            Seek: seconds =>
            {
                Calls.Add($"Seek({seconds})");
                Seeks.Add(seconds);
                if (SeekException != null) throw SeekException;
                return SeekResult;
            },
            Stop: () => { Calls.Add("Stop"); StopCalls++; return PlaybackResult.Ok; },
            SetPaused: paused =>
            {
                Calls.Add($"SetPaused({paused})");
                SetPausedCalls.Add(paused);
                return PlaybackResult.Ok;
            },
            ResetPlayerStateForNewTrack: () => Calls.Add("ResetPlayerStateForNewTrack"),
            ClearLoadedTrackId: () => Calls.Add("ClearLoadedTrackId"),
            HasTimelinePanel: () => { Calls.Add("HasTimelinePanel"); return HasTimelinePanel; },
            ClearTimelineLoadedTrackId: () => Calls.Add("ClearTimelineLoadedTrackId"),
            SetSeekBarValueFromPlayer: value => Calls.Add($"SetSeekBarValueFromPlayer({value})"),
            SetTimeLabel: value => Calls.Add($"SetTimeLabel({value})"),
            SetPlayPauseIcon: value => Calls.Add($"SetPlayPauseIcon({value})"),
            ResetGapFreezeAll: () => Calls.Add("ResetGapFreezeAll"),
            ResetGapFreeze: () => Calls.Add("ResetGapFreeze"),
            ClearGapFreezeFrame: () => Calls.Add("ClearGapFreezeFrame"),
            BeginSyncFileLoad: startPosition =>
            {
                Calls.Add($"BeginSyncFileLoad({startPosition})");
                BeginSyncFileLoads.Add(startPosition);
            },
            // D33-b: ロード成功後のメタデータ取得予約。
            RequestMetadataFetch: () => Calls.Add("RequestMetadataFetch"));
    }

    private static PlaybackOperationsCoordinator Create(Recorder recorder) =>
        new(new PlaybackControlState(), recorder.Build());

    [Fact]
    public void LoadFile_WithoutStart_OnSuccessUnpausesThenResetsStateInOrder()
    {
        var recorder = new Recorder();
        var coordinator = Create(recorder);

        bool result = coordinator.LoadFile("C:\\media\\clip.mp4");

        result.Should().BeTrue();
        recorder.Loads.Should().ContainSingle()
            .Which.Should().Be(("C:\\media\\clip.mp4", null, false));
        recorder.Calls.Should().Equal(
            "IsPlayerReady",
            "Load(C:\\media\\clip.mp4,null,False)",
            "SetPaused(False)",
            "SetPlayPauseIcon(⏸)",
            "ResetPlayerStateForNewTrack",
            "ResetGapFreeze",
            "SetSeekBarValueFromPlayer(0)",
            "SetTimeLabel(0:00 / 0:00)",
            "BeginSyncFileLoad(0)",
            "RequestMetadataFetch");
        recorder.BeginSyncFileLoads.Should().Equal(0);
    }

    [Fact]
    public void LoadFile_OnSuccessRequestsMetadataFetchOnce()
    {
        // D33-b: 速いロードでもタイマーを待たずに 1 回取得を予約する。
        var recorder = new Recorder();
        var coordinator = Create(recorder);

        coordinator.LoadFile("C:\\media\\clip.mp4").Should().BeTrue();
        coordinator.LoadFile("C:\\media\\clip.mp4", 12.5).Should().BeTrue();
        coordinator.LoadFilePaused("C:\\media\\clip.mp4").Should().BeTrue();

        recorder.Calls.Count(call => call == "RequestMetadataFetch").Should().Be(3);
    }

    [Fact]
    public void LoadFile_OnFailure_DoesNotRequestMetadataFetch()
    {
        var recorder = new Recorder { LoadResult = PlaybackResult.Fail("load failed") };
        var coordinator = Create(recorder);

        coordinator.LoadFile("clip.mp4").Should().BeFalse();

        recorder.Calls.Should().NotContain("RequestMetadataFetch");
    }

    [Fact]
    public void LoadFile_WithStart_DoesNotBeginSyncFileLoadGate()
    {
        // 位置つきロード（同期主導のトラック切替）は ContinueOnTrackCoordinator が記録する。
        var recorder = new Recorder();
        var coordinator = Create(recorder);

        coordinator.LoadFile("C:\\media\\clip.mp4", 12.5).Should().BeTrue();

        recorder.BeginSyncFileLoads.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LoadFile_WithStart_KeepsNativePauseAndUiConsistent(bool paused)
    {
        var recorder = new Recorder();
        var playback = new PlaybackControlState();
        playback.SetPaused(paused);
        var coordinator = new PlaybackOperationsCoordinator(playback, recorder.Build());

        bool result = coordinator.LoadFile("C:\\media\\clip.mp4", 12.5);

        result.Should().BeTrue();
        recorder.Loads.Should().ContainSingle()
            .Which.Should().Be(("C:\\media\\clip.mp4", (double?)12.5, paused));
        recorder.SetPausedCalls.Should().Equal(paused);
        playback.IsPaused.Should().Be(paused);
        recorder.Calls.Should().ContainInOrder(
            $"SetPlayPauseIcon({(paused ? "▶" : "⏸")})",
            "ResetPlayerStateForNewTrack",
            "ResetGapFreeze");
    }

    [Fact]
    public void LoadFilePaused_OnSuccessLoadsThenPausesWithoutPlayWrite()
    {
        var recorder = new Recorder();
        var coordinator = Create(recorder);

        bool result = coordinator.LoadFilePaused("C:\\media\\clip.mp4");

        result.Should().BeTrue();
        recorder.Loads.Should().ContainSingle()
            .Which.Should().Be(("C:\\media\\clip.mp4", null, true));
        recorder.SetPausedCalls.Should().Equal(true);
        recorder.Calls.Should().ContainInOrder(
            "Load(C:\\media\\clip.mp4,null,True)",
            "SetPaused(True)",
            "SetPlayPauseIcon(▶)",
            "ResetPlayerStateForNewTrack");
        recorder.SetPausedCalls.Should().NotContain(false);
        recorder.BeginSyncFileLoads.Should().Equal(0);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(12.5, false)]
    public void LoadFile_OnLoadFailureReturnsFalseWithoutResettingState(
        double? startPosition,
        bool writesPause)
    {
        var recorder = new Recorder { LoadResult = PlaybackResult.Fail("load failed") };
        var coordinator = Create(recorder);

        bool result = coordinator.LoadFile("clip.mp4", startPosition);

        result.Should().BeFalse();
        recorder.SetPausedCalls.Any().Should().Be(writesPause);
        recorder.BeginSyncFileLoads.Should().BeEmpty();
        recorder.Calls.Should().NotContain(call =>
            call.StartsWith("SetPlayPauseIcon", StringComparison.Ordinal) ||
            call == "ResetPlayerStateForNewTrack" ||
            call == "ResetGapFreeze");
    }

    [Fact]
    public void LoadFile_WhenPlayerIsNotReadyReturnsFalseWithoutOtherEffects()
    {
        var recorder = new Recorder { IsPlayerReady = false };
        var coordinator = Create(recorder);

        coordinator.LoadFile("clip.mp4").Should().BeFalse();

        recorder.Calls.Should().Equal("IsPlayerReady");
    }

    [Fact]
    public void SeekTo_PassesRawSecondsToTypedApi()
    {
        var recorder = new Recorder();
        var coordinator = Create(recorder);

        coordinator.SeekTo(12.3456).Should().BeTrue();

        recorder.Seeks.Should().Equal(12.3456);
        recorder.Calls.Should().NotContain("IsPlayerReady");
    }

    [Fact]
    public void SeekTo_WhenSeekFailsReturnsFalse()
    {
        var recorder = new Recorder { SeekResult = PlaybackResult.Fail("seek rejected") };

        Create(recorder).SeekTo(1).Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SeekTo_AfterNativeEofPause_ReappliesIntendedPlaybackState(bool paused)
    {
        var recorder = new Recorder();
        var playback = new PlaybackControlState();
        playback.SetPaused(paused);
        var coordinator = new PlaybackOperationsCoordinator(playback, recorder.Build());

        coordinator.SeekTo(5).Should().BeTrue();

        recorder.SetPausedCalls.Should().Equal(paused);
        playback.IsPaused.Should().Be(paused);
    }

    [Fact]
    public void SeekTo_WhenSeekThrowsReturnsFalse()
    {
        var recorder = new Recorder { SeekException = new InvalidOperationException("test") };

        Create(recorder).SeekTo(1).Should().BeFalse();
    }

    [Fact]
    public void StopPlayback_ResetsPlaybackStateInOriginalOrder()
    {
        var recorder = new Recorder();
        var coordinator = Create(recorder);

        coordinator.StopPlayback();

        recorder.StopCalls.Should().Be(1);
        recorder.Calls.Should().Equal(
            "IsPlayerReady",
            "Stop",
            "SetPaused(True)",
            "SetPlayPauseIcon(▶)",
            "ResetPlayerStateForNewTrack",
            "ClearLoadedTrackId",
            "HasTimelinePanel",
            "ClearTimelineLoadedTrackId",
            "SetSeekBarValueFromPlayer(0)",
            "SetTimeLabel(0:00 / 0:00)",
            "SetPlayPauseIcon(▶)",
            "ResetGapFreezeAll",
            "ClearGapFreezeFrame");
    }

    [Fact]
    public void StopPlayback_WithoutTimelinePanelSkipsTimelineReset()
    {
        var recorder = new Recorder { HasTimelinePanel = false };

        Create(recorder).StopPlayback();

        recorder.Calls.Should().Contain("HasTimelinePanel");
        recorder.Calls.Should().NotContain("ClearTimelineLoadedTrackId");
    }

    [Fact]
    public void StopPlayback_WhenPlayerIsNotReadyDoesNothingFurther()
    {
        var recorder = new Recorder { IsPlayerReady = false };

        Create(recorder).StopPlayback();

        recorder.Calls.Should().Equal("IsPlayerReady");
    }
}
