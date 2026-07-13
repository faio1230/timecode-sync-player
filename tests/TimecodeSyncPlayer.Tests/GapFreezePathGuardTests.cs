using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public class GapFreezePathGuardTests
{
    private static readonly IntPtr s_mpv = new(123);

    [Fact]
    public void Check_ReturnsExpected_WhenPendingPathIsEmpty()
    {
        var api = new FakeMpvApi();
        DateTime lastReloadAt = DateTime.UtcNow;

        GapFreezePathCheckResult result = GapFreezePathGuard.Check(
            api,
            s_mpv,
            pendingPath: null,
            pendingTargetSeconds: 12.0,
            lastReloadAt,
            now: lastReloadAt.AddSeconds(10),
            reloadDebounce: TimeSpan.FromSeconds(1));

        result.IsExpected.Should().BeTrue();
        result.ReloadIssued.Should().BeFalse();
        result.LastReloadAt.Should().Be(lastReloadAt);
        api.Commands.Should().BeEmpty();
    }

    [Fact]
    public void Check_ReturnsExpected_WhenCurrentPathMatchesPendingPath()
    {
        var api = new FakeMpvApi { CurrentPath = "C:\\Videos\\clip.mp4" };
        DateTime lastReloadAt = DateTime.UtcNow;

        GapFreezePathCheckResult result = GapFreezePathGuard.Check(
            api,
            s_mpv,
            pendingPath: "C:\\Videos\\clip.mp4",
            pendingTargetSeconds: 12.0,
            lastReloadAt,
            now: lastReloadAt.AddSeconds(10),
            reloadDebounce: TimeSpan.FromSeconds(1));

        result.IsExpected.Should().BeTrue();
        result.ReloadIssued.Should().BeFalse();
        api.Commands.Should().BeEmpty();
    }

    [Fact]
    public void Check_ReturnsUnexpectedWithoutReload_WhenWithinDebounce()
    {
        var api = new FakeMpvApi { CurrentPath = "C:\\Videos\\other.mp4" };
        DateTime now = DateTime.UtcNow;
        DateTime lastReloadAt = now.AddMilliseconds(-500);

        GapFreezePathCheckResult result = GapFreezePathGuard.Check(
            api,
            s_mpv,
            pendingPath: "C:\\Videos\\clip.mp4",
            pendingTargetSeconds: 12.0,
            lastReloadAt,
            now,
            reloadDebounce: TimeSpan.FromSeconds(1));

        result.IsExpected.Should().BeFalse();
        result.ReloadIssued.Should().BeFalse();
        result.LastReloadAt.Should().Be(lastReloadAt);
        api.Commands.Should().BeEmpty();
    }

    [Fact]
    public void Check_ReissuesLoadAndPause_WhenDebounceElapsed()
    {
        var api = new FakeMpvApi { CurrentPath = "C:\\Videos\\other.mp4" };
        DateTime now = DateTime.UtcNow;
        DateTime lastReloadAt = now.AddSeconds(-2);

        GapFreezePathCheckResult result = GapFreezePathGuard.Check(
            api,
            s_mpv,
            pendingPath: "C:\\Videos\\clip.mp4",
            pendingTargetSeconds: 12.345,
            lastReloadAt,
            now,
            reloadDebounce: TimeSpan.FromSeconds(1));

        result.IsExpected.Should().BeFalse();
        result.ReloadIssued.Should().BeTrue();
        result.LastReloadAt.Should().Be(now);
        result.LoadRc.Should().Be(0);
        result.PauseRc.Should().Be(0);
        api.Commands.Should().ContainSingle()
            .Which.Should().Contain("loadfile");
        api.Commands[0].Should().Contain("12.345");
        api.SetProperties.Should().ContainSingle()
            .Which.Should().Be(("pause", "yes"));
    }

    private sealed class FakeMpvApi : IMpvApi
    {
        public string CurrentPath { get; init; } = "";
        public List<string> Commands { get; } = [];
        public List<(string Name, string Value)> SetProperties { get; } = [];

        public IntPtr Create() => s_mpv;
        public int Initialize(IntPtr ctx) => 0;
        public void TerminateDestroy(IntPtr ctx) { }
        public int SetPropertyString(IntPtr ctx, string name, string value)
        {
            SetProperties.Add((name, value));
            return 0;
        }

        public int GetProperty(IntPtr ctx, string name, int format, out double result)
        {
            result = 0;
            return -1;
        }

        public string GetPropertyString(IntPtr ctx, string name) => CurrentPath;

        public int CommandString(IntPtr ctx, string args)
        {
            Commands.Add(args);
            return 0;
        }

        public void Free(IntPtr data) { }
        public int FormatDouble => 4;
    }
}
