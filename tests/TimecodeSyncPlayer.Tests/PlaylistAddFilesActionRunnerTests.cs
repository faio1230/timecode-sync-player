using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistAddFilesActionRunnerTests
{
    [Fact]
    public async Task RunAsync_LoadsCurrentTrack_WhenPlaylistWasEmptyAndTrackExists()
    {
        var calls = new List<string>();
        var runner = new PlaylistAddFilesActionRunner();

        await runner.RunAsync(
            wasEmpty: true,
            addFilesAsync: () =>
            {
                calls.Add("add");
                return Task.CompletedTask;
            },
            logError: _ => calls.Add("logError"),
            showError: () => calls.Add("showError"),
            syncSelection: () => calls.Add("syncSelection"),
            updateTimelineDisplay: () => calls.Add("updateTimeline"),
            hasCurrentTrack: () =>
            {
                calls.Add("hasCurrentTrack");
                return true;
            },
            loadCurrentTrack: () => calls.Add("loadCurrentTrack"));

        calls.Should().Equal(
            "add",
            "syncSelection",
            "updateTimeline",
            "hasCurrentTrack",
            "loadCurrentTrack");
    }

    [Fact]
    public async Task RunAsync_DoesNotLoadCurrentTrack_WhenPlaylistWasNotEmpty()
    {
        var calls = new List<string>();
        var runner = new PlaylistAddFilesActionRunner();

        await runner.RunAsync(
            wasEmpty: false,
            addFilesAsync: () =>
            {
                calls.Add("add");
                return Task.CompletedTask;
            },
            logError: _ => calls.Add("logError"),
            showError: () => calls.Add("showError"),
            syncSelection: () => calls.Add("syncSelection"),
            updateTimelineDisplay: () => calls.Add("updateTimeline"),
            hasCurrentTrack: () =>
            {
                calls.Add("hasCurrentTrack");
                return true;
            },
            loadCurrentTrack: () => calls.Add("loadCurrentTrack"));

        calls.Should().Equal("add", "syncSelection", "updateTimeline");
    }

    [Fact]
    public async Task RunAsync_DoesNotLoadCurrentTrack_WhenNoCurrentTrackExists()
    {
        var calls = new List<string>();
        var runner = new PlaylistAddFilesActionRunner();

        await runner.RunAsync(
            wasEmpty: true,
            addFilesAsync: () =>
            {
                calls.Add("add");
                return Task.CompletedTask;
            },
            logError: _ => calls.Add("logError"),
            showError: () => calls.Add("showError"),
            syncSelection: () => calls.Add("syncSelection"),
            updateTimelineDisplay: () => calls.Add("updateTimeline"),
            hasCurrentTrack: () =>
            {
                calls.Add("hasCurrentTrack");
                return false;
            },
            loadCurrentTrack: () => calls.Add("loadCurrentTrack"));

        calls.Should().Equal("add", "syncSelection", "updateTimeline", "hasCurrentTrack");
    }

    [Fact]
    public async Task RunAsync_LogsAndShowsErrorWithoutContinuing_WhenAddThrows()
    {
        var expected = new InvalidOperationException("add failed");
        var calls = new List<string>();
        var runner = new PlaylistAddFilesActionRunner();

        await runner.RunAsync(
            wasEmpty: true,
            addFilesAsync: () => throw expected,
            logError: ex =>
            {
                ex.Should().BeSameAs(expected);
                calls.Add("logError");
            },
            showError: () => calls.Add("showError"),
            syncSelection: () => calls.Add("syncSelection"),
            updateTimelineDisplay: () => calls.Add("updateTimeline"),
            hasCurrentTrack: () =>
            {
                calls.Add("hasCurrentTrack");
                return true;
            },
            loadCurrentTrack: () => calls.Add("loadCurrentTrack"));

        calls.Should().Equal("logError", "showError");
    }
}
