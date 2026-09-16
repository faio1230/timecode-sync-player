using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public class GapPlaybackCommandExecutorTests
{
    [Fact]
    public void PauseForGap_PausesPlayback()
    {
        var api = new FakePlaybackApi();
        var executor = new GapPlaybackCommandExecutor(api);

        PlaybackResult result = executor.PauseForGap();

        result.Success.Should().BeTrue();
        api.SetPausedCalls.Should().Equal(true);
    }

    [Fact]
    public void PauseForGap_NativeFailure_ReturnsFailure()
    {
        var api = new FakePlaybackApi { SetPausedResult = PlaybackResult.Fail("pause failed") };
        var executor = new GapPlaybackCommandExecutor(api);

        executor.PauseForGap().Success.Should().BeFalse();
    }

    [Fact]
    public void LoadPausedAt_LoadsFileAtTargetPaused_ThenPauses()
    {
        var api = new FakePlaybackApi();
        var executor = new GapPlaybackCommandExecutor(api);

        GapLoadCommandResult result = executor.LoadPausedAt(@"C:\media\track.mp4", 12.345);

        result.Load.Success.Should().BeTrue();
        result.Pause.Success.Should().BeTrue();
        api.Loads.Should().ContainSingle()
            .Which.Should().Be((@"C:\media\track.mp4", (double?)12.345, true));
        api.SetPausedCalls.Should().Equal(true);
    }

    [Fact]
    public void LoadPausedAt_NativeLoadFailure_ReturnsFailure()
    {
        var api = new FakePlaybackApi { LoadResult = PlaybackResult.Fail("load failed") };
        var executor = new GapPlaybackCommandExecutor(api);

        GapLoadCommandResult result = executor.LoadPausedAt("C:/missing.mp4", 1.0);

        result.Load.Success.Should().BeFalse();
        result.Load.Error.Should().Be("load failed");
        result.Pause.Success.Should().BeTrue();
    }
}
