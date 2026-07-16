using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistDurationBackfillCoordinatorTests
{
    [Fact]
    public async Task BackfillAsync_ForwardsSnapshotInputsAndAppliesDuration()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles(["existing.mp4", "new.mp4"], autoOffset: false);
        var reader = new RecordingDurationReader
        {
            Durations = { ["new.mp4"] = TimeSpan.FromSeconds(42) }
        };
        var calls = new List<string>();
        var coordinator = new PlaylistDurationBackfillCoordinator(
            new PlaylistDurationBackfillService(reader),
            new PlaylistDurationBackfillEffects(
                GetTracks: () =>
                {
                    calls.Add("get-tracks");
                    return playlist.Tracks;
                },
                ApplyDurationOnUiAsync: (trackId, duration, recalculateTimeline) =>
                {
                    calls.Add($"apply:{trackId}:{duration.TotalSeconds}");
                    playlist.UpdateMediaDuration(trackId, duration, recalculateTimeline);
                    return Task.CompletedTask;
                },
                HandleFailure: _ => calls.Add("failure")));

        await coordinator.BackfillAsync(["new.mp4"], startIndex: 1);

        reader.Paths.Should().Equal("new.mp4");
        playlist.Tracks[0].MediaDuration.Should().Be(TimeSpan.Zero);
        playlist.Tracks[1].MediaDuration.Should().Be(TimeSpan.FromSeconds(42));
        calls.Should().Equal(
            "get-tracks",
            $"apply:{playlist.Tracks[1].Id}:42");
    }

    [Fact]
    public async Task BackfillAsync_PreservesSavedTimelineOffsets_WhenRecalculationIsDisabled()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles(["a.mp4", "b.mp4"], autoOffset: false);
        playlist.Tracks[0] = playlist.Tracks[0] with { TimelineOffset = TimeSpan.FromSeconds(10) };
        playlist.Tracks[1] = playlist.Tracks[1] with { TimelineOffset = TimeSpan.FromSeconds(30) };
        var reader = new RecordingDurationReader
        {
            Durations =
            {
                ["a.mp4"] = TimeSpan.FromSeconds(20),
                ["b.mp4"] = TimeSpan.FromSeconds(20)
            }
        };
        var coordinator = new PlaylistDurationBackfillCoordinator(
            new PlaylistDurationBackfillService(reader),
            new PlaylistDurationBackfillEffects(
                GetTracks: () => playlist.Tracks,
                ApplyDurationOnUiAsync: (trackId, duration, recalculateTimeline) =>
                {
                    playlist.UpdateMediaDuration(trackId, duration, recalculateTimeline);
                    return Task.CompletedTask;
                },
                HandleFailure: _ => { }));

        await coordinator.BackfillAsync(
            ["a.mp4", "b.mp4"],
            recalculateTimeline: false);

        playlist.Tracks.Select(track => track.MediaDuration)
            .Should().Equal(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
        playlist.Tracks.Select(track => track.TimelineOffset)
            .Should().Equal(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task BackfillAsync_NullDurationDoesNotApplyOrFail()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles(["unknown.mp4"]);
        var calls = new List<string>();
        var coordinator = new PlaylistDurationBackfillCoordinator(
            new PlaylistDurationBackfillService(new RecordingDurationReader()),
            new PlaylistDurationBackfillEffects(
                GetTracks: () => playlist.Tracks,
                ApplyDurationOnUiAsync: (_, _, _) =>
                {
                    calls.Add("apply");
                    return Task.CompletedTask;
                },
                HandleFailure: _ => calls.Add("failure")));

        await coordinator.BackfillAsync(["unknown.mp4"]);

        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task BackfillAsync_ReaderFailureInvokesFailureEffectWithSameException()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles(["broken.mp4"]);
        var expected = new InvalidOperationException("duration failed");
        Exception? captured = null;
        var coordinator = new PlaylistDurationBackfillCoordinator(
            new PlaylistDurationBackfillService(new ThrowingDurationReader(expected)),
            new PlaylistDurationBackfillEffects(
                GetTracks: () => playlist.Tracks,
                ApplyDurationOnUiAsync: (_, _, _) => Task.CompletedTask,
                HandleFailure: ex => captured = ex));

        await coordinator.BackfillAsync(["broken.mp4"]);

        captured.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task BackfillAsync_UiApplyFailureInvokesFailureEffect()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles(["clip.mp4"]);
        var expected = new InvalidOperationException("dispatcher failed");
        Exception? captured = null;
        var reader = new RecordingDurationReader
        {
            Durations = { ["clip.mp4"] = TimeSpan.FromSeconds(10) }
        };
        var coordinator = new PlaylistDurationBackfillCoordinator(
            new PlaylistDurationBackfillService(reader),
            new PlaylistDurationBackfillEffects(
                GetTracks: () => playlist.Tracks,
                ApplyDurationOnUiAsync: (_, _, _) => throw expected,
                HandleFailure: ex => captured = ex));

        await coordinator.BackfillAsync(["clip.mp4"]);

        captured.Should().BeSameAs(expected);
    }

    private sealed class RecordingDurationReader : IMediaDurationReader
    {
        public Dictionary<string, TimeSpan> Durations { get; } = [];
        public List<string> Paths { get; } = [];

        public Task<TimeSpan?> ReadDurationAsync(string path)
        {
            Paths.Add(path);
            return Task.FromResult(Durations.TryGetValue(path, out TimeSpan duration)
                ? duration
                : (TimeSpan?)null);
        }
    }

    private sealed class ThrowingDurationReader : IMediaDurationReader
    {
        private readonly Exception _exception;

        public ThrowingDurationReader(Exception exception)
        {
            _exception = exception;
        }

        public Task<TimeSpan?> ReadDurationAsync(string path) => throw _exception;
    }
}
