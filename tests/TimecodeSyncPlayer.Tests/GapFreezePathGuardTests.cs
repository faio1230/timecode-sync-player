using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public class GapFreezePathGuardTests
{
    [Fact]
    public void Check_ReturnsExpected_WhenPendingPathIsEmpty()
    {
        var api = new FakePlaybackApi();
        DateTime lastReloadAt = DateTime.UtcNow;

        GapFreezePathCheckResult result = GapFreezePathGuard.Check(
            api,
            pendingPath: null,
            pendingTargetSeconds: 12.0,
            lastReloadAt,
            now: lastReloadAt.AddSeconds(10),
            reloadDebounce: TimeSpan.FromSeconds(1));

        result.IsExpected.Should().BeTrue();
        result.ReloadIssued.Should().BeFalse();
        result.LastReloadAt.Should().Be(lastReloadAt);
        api.Loads.Should().BeEmpty();
        api.SetPausedCalls.Should().BeEmpty();
    }

    [Fact]
    public void Check_ReturnsExpected_WhenCurrentPathMatchesPendingPath()
    {
        var api = new FakePlaybackApi { Path = "C:\\Videos\\clip.mp4" };
        DateTime lastReloadAt = DateTime.UtcNow;

        GapFreezePathCheckResult result = GapFreezePathGuard.Check(
            api,
            pendingPath: "C:\\Videos\\clip.mp4",
            pendingTargetSeconds: 12.0,
            lastReloadAt,
            now: lastReloadAt.AddSeconds(10),
            reloadDebounce: TimeSpan.FromSeconds(1));

        result.IsExpected.Should().BeTrue();
        result.ReloadIssued.Should().BeFalse();
        api.Loads.Should().BeEmpty();
    }

    [Fact]
    public void Check_ReturnsUnexpectedWithoutReload_WhenWithinDebounce()
    {
        var api = new FakePlaybackApi { Path = "C:\\Videos\\other.mp4" };
        DateTime now = DateTime.UtcNow;
        DateTime lastReloadAt = now.AddMilliseconds(-500);

        GapFreezePathCheckResult result = GapFreezePathGuard.Check(
            api,
            pendingPath: "C:\\Videos\\clip.mp4",
            pendingTargetSeconds: 12.0,
            lastReloadAt,
            now,
            reloadDebounce: TimeSpan.FromSeconds(1));

        result.IsExpected.Should().BeFalse();
        result.ReloadIssued.Should().BeFalse();
        result.LastReloadAt.Should().Be(lastReloadAt);
        api.Loads.Should().BeEmpty();
    }

    [Fact]
    public void Check_ReissuesLoadAndPause_WhenDebounceElapsed()
    {
        var api = new FakePlaybackApi { Path = "C:\\Videos\\other.mp4" };
        DateTime now = DateTime.UtcNow;
        DateTime lastReloadAt = now.AddSeconds(-2);

        GapFreezePathCheckResult result = GapFreezePathGuard.Check(
            api,
            pendingPath: "C:\\Videos\\clip.mp4",
            pendingTargetSeconds: 12.345,
            lastReloadAt,
            now,
            reloadDebounce: TimeSpan.FromSeconds(1));

        result.IsExpected.Should().BeFalse();
        result.ReloadIssued.Should().BeTrue();
        result.LastReloadAt.Should().Be(now);
        result.Load!.Value.Success.Should().BeTrue();
        result.Pause!.Value.Success.Should().BeTrue();
        api.Loads.Should().ContainSingle()
            .Which.Should().Be(("C:\\Videos\\clip.mp4", (double?)12.345, true));
        api.SetPausedCalls.Should().Equal(true);
    }

    [Fact]
    public void Check_ReissuesLoad_ReturnsLoadFailure()
    {
        var api = new FakePlaybackApi
        {
            Path = "C:\\Videos\\other.mp4",
            LoadResult = PlaybackResult.Fail("load failed"),
        };
        DateTime now = DateTime.UtcNow;

        GapFreezePathCheckResult result = GapFreezePathGuard.Check(
            api,
            pendingPath: "C:\\Videos\\clip.mp4",
            pendingTargetSeconds: 12.345,
            lastReloadAt: now.AddSeconds(-2),
            now,
            reloadDebounce: TimeSpan.FromSeconds(1));

        result.ReloadIssued.Should().BeTrue();
        result.Load!.Value.Success.Should().BeFalse();
        result.Load.Value.Error.Should().Be("load failed");
    }
}
