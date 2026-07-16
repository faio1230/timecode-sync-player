using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistDurationBackfillServiceTests
{
    [Fact]
    public async Task BackfillAsync_UpdatesTracksWithReadDurations()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles(["C:\\Videos\\a.mp4", "C:\\Videos\\b.mp4"]);
        var reader = new FakeDurationReader
        {
            Durations =
            {
                ["C:\\Videos\\a.mp4"] = TimeSpan.FromSeconds(10),
                ["C:\\Videos\\b.mp4"] = TimeSpan.FromSeconds(20)
            }
        };
        var service = new PlaylistDurationBackfillService(reader);

        await service.BackfillAsync(
            playlist.Tracks,
            playlist.Tracks.Select(t => t.FilePath).ToList(),
            applyDurationAsync: ApplyDurationAsync(playlist, autoOffset: true));

        playlist.Tracks[0].MediaDuration.Should().Be(TimeSpan.FromSeconds(10));
        playlist.Tracks[1].MediaDuration.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task BackfillAsync_LeavesTrackUnchanged_WhenDurationIsNull()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles(["C:\\Videos\\a.mp4"]);
        var service = new PlaylistDurationBackfillService(new FakeDurationReader());

        await service.BackfillAsync(
            playlist.Tracks,
            playlist.Tracks.Select(t => t.FilePath).ToList(),
            applyDurationAsync: ApplyDurationAsync(playlist, autoOffset: true));

        playlist.Tracks[0].MediaDuration.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task BackfillAsync_WithUnknownAndKnownDurations_AppliesOnlyKnownDuration()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles(["C:\\Videos\\unknown.mp4", "C:\\Videos\\known.mp4"], autoOffset: false);
        playlist.Tracks[0] = playlist.Tracks[0] with { TimelineOffset = TimeSpan.FromSeconds(7) };
        playlist.Tracks[1] = playlist.Tracks[1] with { TimelineOffset = TimeSpan.FromSeconds(20) };
        var reader = new FakeDurationReader
        {
            Durations =
            {
                ["C:\\Videos\\known.mp4"] = TimeSpan.FromSeconds(12),
            },
        };
        var service = new PlaylistDurationBackfillService(reader);

        await service.BackfillAsync(
            playlist.Tracks,
            playlist.Tracks.Select(track => track.FilePath).ToList(),
            applyDurationAsync: ApplyDurationAsync(playlist, autoOffset: false));

        playlist.Tracks[0].MediaDuration.Should().Be(TimeSpan.Zero);
        playlist.Tracks[0].TimelineOffset.Should().Be(TimeSpan.FromSeconds(7));
        playlist.Tracks[1].MediaDuration.Should().Be(TimeSpan.FromSeconds(12));
        playlist.Tracks[1].TimelineOffset.Should().Be(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task BackfillAsync_UsesStartIndexForSnapshot()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles(["C:\\Videos\\existing.mp4", "C:\\Videos\\new.mp4"], autoOffset: false);
        var reader = new FakeDurationReader
        {
            Durations =
            {
                ["C:\\Videos\\new.mp4"] = TimeSpan.FromSeconds(30)
            }
        };
        var service = new PlaylistDurationBackfillService(reader);

        await service.BackfillAsync(
            playlist.Tracks,
            ["C:\\Videos\\new.mp4"],
            startIndex: 1,
            applyDurationAsync: ApplyDurationAsync(playlist, autoOffset: true));

        playlist.Tracks[0].MediaDuration.Should().Be(TimeSpan.Zero);
        playlist.Tracks[1].MediaDuration.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task BackfillAsync_UsesSnapshotTrack_WhenPlaylistSelectionChangesDuringRead()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles(["C:\\Videos\\a.mp4", "C:\\Videos\\b.mp4"], autoOffset: false);
        var firstDuration = new TaskCompletionSource<TimeSpan?>();
        var reader = new FakeDurationReader();
        reader.PendingDurations["C:\\Videos\\a.mp4"] = firstDuration;
        reader.Durations["C:\\Videos\\b.mp4"] = TimeSpan.FromSeconds(20);
        var service = new PlaylistDurationBackfillService(reader);

        Task backfillTask = service.BackfillAsync(
            playlist.Tracks,
            playlist.Tracks.Select(t => t.FilePath).ToList(),
            applyDurationAsync: ApplyDurationAsync(playlist, autoOffset: true));
        playlist.Select(1);
        firstDuration.SetResult(TimeSpan.FromSeconds(10));
        await backfillTask;

        playlist.Tracks[0].MediaDuration.Should().Be(TimeSpan.FromSeconds(10));
        playlist.Tracks[1].MediaDuration.Should().Be(TimeSpan.FromSeconds(20));
        playlist.CurrentIndex.Should().Be(1);
    }

    private static Func<Guid, TimeSpan, Task> ApplyDurationAsync(PlaylistState playlist, bool autoOffset)
    {
        return (trackId, duration) =>
        {
            playlist.UpdateMediaDuration(trackId, duration, recalculate: autoOffset);
            return Task.CompletedTask;
        };
    }

    private sealed class FakeDurationReader : IMediaDurationReader
    {
        public Dictionary<string, TimeSpan> Durations { get; } = [];
        public Dictionary<string, TaskCompletionSource<TimeSpan?>> PendingDurations { get; } = [];

        public Task<TimeSpan?> ReadDurationAsync(string path)
        {
            if (PendingDurations.TryGetValue(path, out TaskCompletionSource<TimeSpan?>? pending))
                return pending.Task;

            return Task.FromResult(Durations.TryGetValue(path, out TimeSpan duration)
                ? duration
                : (TimeSpan?)null);
        }
    }
}
