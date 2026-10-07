using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Gst;
using TimecodeSyncPlayer.ViewModels;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.6.6 F-7: クリップの長さの取り方の順。
/// 軽い関数（追加・開く）→ 読み込んだときの再生時の長さ（TryGetDuration）→ 不明（0 のまま、表示は「--」）。
/// </summary>
public class PlaylistDurationFallbackTests
{
    private sealed class MapDurationReader : IMediaDurationReader
    {
        public Dictionary<string, TimeSpan> Durations { get; } = [];
        public List<string> Calls { get; } = [];

        public Task<TimeSpan?> ReadDurationAsync(string filePath)
        {
            Calls.Add(filePath);
            return Task.FromResult(Durations.TryGetValue(filePath, out TimeSpan d) ? d : (TimeSpan?)null);
        }
    }

    private static (PlaylistState Playlist, PlaylistViewModel Vm, PlaylistDurationFallback Fallback) Setup(
        MapDurationReader reader, bool autoOffset = true)
    {
        var playlist = new PlaylistState();
        var fallback = new PlaylistDurationFallback();
        var vm = new PlaylistViewModel(playlist, reader)
        {
            AutoOffsetOnAdd = autoOffset,
            DurationUnavailable = fallback.MarkUnavailable,
        };
        return (playlist, vm, fallback);
    }

    [Fact]
    public async Task First_TheContainerProbe_SetsTheLength_AndTheLoadedLengthIsNotUsed()
    {
        var reader = new MapDurationReader { Durations = { [@"C:\m\a.mp4"] = TimeSpan.FromSeconds(10) } };
        var (playlist, vm, fallback) = Setup(reader);
        await vm.AddFilesAsync([@"C:\m\a.mp4"], CancellationToken.None);

        bool applied = fallback.TryApplyFromLoadedMedia(
            playlist, playlist.Tracks[0].Id, () => @"C:\m\a.mp4", 99.0);

        applied.Should().BeFalse("a length from the container probe is never overwritten");
        playlist.Tracks[0].MediaDuration.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Second_WhenTheProbeGaveNone_TheLoadedLengthFillsTheRow_AndReplacesLaterOffsets()
    {
        var reader = new MapDurationReader
        {
            Durations = { [@"C:\m\b.mp4"] = TimeSpan.FromSeconds(20), [@"C:\m\c.mp4"] = TimeSpan.FromSeconds(5) },
        };
        var (playlist, vm, fallback) = Setup(reader, autoOffset: true);
        await vm.AddFilesAsync([@"C:\m\a.mp4", @"C:\m\b.mp4", @"C:\m\c.mp4"], CancellationToken.None);
        playlist.Tracks[0].MediaDuration.Should().Be(TimeSpan.Zero);
        playlist.Tracks[1].TimelineOffset.Should().Be(TimeSpan.Zero, "the first row has no length yet");

        bool applied = fallback.TryApplyFromLoadedMedia(
            playlist, playlist.Tracks[0].Id, () => @"C:\m\a.mp4", 12.5);

        applied.Should().BeTrue();
        playlist.Tracks[0].MediaDuration.Should().Be(TimeSpan.FromSeconds(12.5));
        playlist.Tracks[0].TimelineOffset.Should().Be(TimeSpan.Zero, "the row itself does not move (D39 K1)");
        playlist.Tracks[1].TimelineOffset.Should().Be(TimeSpan.FromSeconds(12.5));
        playlist.Tracks[2].TimelineOffset.Should().Be(TimeSpan.FromSeconds(32.5));
    }

    [Fact]
    public async Task Second_WhenAddedWithoutAutoOffset_LaterOffsetsStay()
    {
        var reader = new MapDurationReader { Durations = { [@"C:\m\b.mp4"] = TimeSpan.FromSeconds(20) } };
        var (playlist, vm, fallback) = Setup(reader, autoOffset: false);
        await vm.AddFilesAsync([@"C:\m\a.mp4", @"C:\m\b.mp4"], CancellationToken.None);
        playlist.Tracks[1] = playlist.Tracks[1] with { TimelineOffset = TimeSpan.FromSeconds(40) };

        fallback.TryApplyFromLoadedMedia(playlist, playlist.Tracks[0].Id, () => @"C:\m\a.mp4", 12.5)
            .Should().BeTrue();

        playlist.Tracks[1].TimelineOffset.Should().Be(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task Second_FromTheProjectPath_DoesNotMoveSavedOffsets()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles([@"C:\m\a.mp4", @"C:\m\b.mp4"], autoOffset: false);
        playlist.Tracks[1] = playlist.Tracks[1] with { TimelineOffset = TimeSpan.FromSeconds(40) };
        var fallback = new PlaylistDurationFallback();
        var service = new PlaylistDurationBackfillService(new MapDurationReader());
        var coordinator = new PlaylistDurationBackfillCoordinator(service, new PlaylistDurationBackfillEffects(
            GetTracks: () => playlist.Tracks,
            ApplyDurationOnUiAsync: (id, d, r) => { playlist.UpdateMediaDuration(id, d, r); return Task.CompletedTask; },
            HandleFailure: ex => throw ex,
            MarkDurationUnavailable: fallback.MarkUnavailable));

        await coordinator.BackfillAsync(playlist.Tracks.Select(t => t.FilePath).ToList(), recalculateTimeline: false);
        fallback.TryApplyFromLoadedMedia(playlist, playlist.Tracks[0].Id, () => @"C:\m\a.mp4", 12.5)
            .Should().BeTrue();

        playlist.Tracks[0].MediaDuration.Should().Be(TimeSpan.FromSeconds(12.5));
        playlist.Tracks[1].TimelineOffset.Should().Be(TimeSpan.FromSeconds(40), "a project keeps its saved offsets");
    }

    [Fact]
    public async Task Second_FromTheOpenFilesPath_ReplacesLaterOffsets()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles([@"C:\m\a.mp4", @"C:\m\b.mp4"], autoOffset: true);
        var fallback = new PlaylistDurationFallback();
        var reader = new MapDurationReader { Durations = { [@"C:\m\b.mp4"] = TimeSpan.FromSeconds(7) } };
        var coordinator = new PlaylistDurationBackfillCoordinator(
            new PlaylistDurationBackfillService(reader),
            new PlaylistDurationBackfillEffects(
                GetTracks: () => playlist.Tracks,
                ApplyDurationOnUiAsync: (id, d, r) => { playlist.UpdateMediaDuration(id, d, r); return Task.CompletedTask; },
                HandleFailure: ex => throw ex,
                MarkDurationUnavailable: fallback.MarkUnavailable));

        await coordinator.BackfillAsync(playlist.Tracks.Select(t => t.FilePath).ToList(), recalculateTimeline: true);
        fallback.TryApplyFromLoadedMedia(playlist, playlist.Tracks[0].Id, () => @"C:\m\a.mp4", 3.0)
            .Should().BeTrue();

        playlist.Tracks[1].TimelineOffset.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Second_IsNotAppliedWhileThePlayerStillHoldsAnotherFile()
    {
        var (playlist, vm, fallback) = Setup(new MapDurationReader());
        await vm.AddFilesAsync([@"C:\m\a.mp4", @"C:\m\b.mp4"], CancellationToken.None);

        fallback.TryApplyFromLoadedMedia(playlist, playlist.Tracks[0].Id, () => @"C:\m\b.mp4", 30.0)
            .Should().BeFalse("the player's length belongs to another file");
        fallback.TryApplyFromLoadedMedia(playlist, playlist.Tracks[0].Id, () => "", 30.0)
            .Should().BeFalse();

        playlist.Tracks[0].MediaDuration.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Third_WhenNeitherGivesALength_TheRowStaysZero_AndShowsUnknown(double playerSeconds)
    {
        var (playlist, vm, fallback) = Setup(new MapDurationReader());
        await vm.AddFilesAsync([@"C:\m\a.mp4"], CancellationToken.None);

        fallback.TryApplyFromLoadedMedia(playlist, playlist.Tracks[0].Id, () => @"C:\m\a.mp4", playerSeconds)
            .Should().BeFalse();

        PlaylistTrack track = playlist.Tracks[0];
        track.MediaDuration.Should().Be(TimeSpan.Zero);
        track.MediaDurationText.Should().Be(PlaylistTrackFormatter.UnknownDurationText);
        track.EffectiveDurationText.Should().Be(PlaylistTrackFormatter.UnknownDurationText);
        track.GetTimelineRangeText().Should().EndWith(PlaylistTrackExtensions.UnknownRangeEndText);
    }

    [Fact]
    public void NoLoadedTrack_DoesNothing_AndDoesNotAskThePlayerPath()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles([@"C:\m\a.mp4"]);
        var fallback = new PlaylistDurationFallback();
        bool askedPath = false;

        fallback.TryApplyFromLoadedMedia(playlist, null, () => { askedPath = true; return @"C:\m\a.mp4"; }, 10)
            .Should().BeFalse();
        askedPath.Should().BeFalse();
    }

    [Fact]
    public void RowWithALength_DoesNotAskThePlayerPath()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles([@"C:\m\a.mp4"]);
        playlist.UpdateMediaDuration(playlist.Tracks[0].Id, TimeSpan.FromSeconds(5));
        var fallback = new PlaylistDurationFallback();
        bool askedPath = false;

        fallback.TryApplyFromLoadedMedia(
                playlist, playlist.Tracks[0].Id, () => { askedPath = true; return @"C:\m\a.mp4"; }, 10)
            .Should().BeFalse();
        askedPath.Should().BeFalse("the player path is a native call; it is only read for rows without a length");
    }

    [Fact]
    public async Task VmReportsTheRowsWithoutALength()
    {
        var reader = new MapDurationReader { Durations = { ["b.mp4"] = TimeSpan.FromSeconds(3) } };
        var playlist = new PlaylistState();
        var reported = new List<(Guid Id, bool Recalculate)>();
        var vm = new PlaylistViewModel(playlist, reader)
        {
            AutoOffsetOnAdd = true,
            DurationUnavailable = (id, r) => reported.Add((id, r)),
        };

        await vm.AddFilesAsync(["a.mp4", "b.mp4"], CancellationToken.None);

        reported.Should().Equal((playlist.Tracks[0].Id, true));
    }

    [Fact]
    public async Task Reader_UsesTheProbe_OffTheCallingThread_AndMapsFailuresToNull()
    {
        int callerThread = Environment.CurrentManagedThreadId;
        int probeThread = -1;
        var values = new Queue<double?>([12.25, null, 0.0, -3.0, double.PositiveInfinity]);
        var reader = new GstMediaDurationReader(_ =>
        {
            probeThread = Environment.CurrentManagedThreadId;
            Thread.Sleep(5);
            return values.Dequeue();
        });

        (await reader.ReadDurationAsync("x.mp4")).Should().Be(TimeSpan.FromSeconds(12.25));
        probeThread.Should().NotBe(callerThread, "the probe reads the file; it must not run on the caller (UI) thread");
        (await reader.ReadDurationAsync("x.mp4")).Should().BeNull();
        (await reader.ReadDurationAsync("x.mp4")).Should().BeNull();
        (await reader.ReadDurationAsync("x.mp4")).Should().BeNull();
        (await reader.ReadDurationAsync("x.mp4")).Should().BeNull();
        (await reader.ReadDurationAsync("")).Should().BeNull();
    }
}
