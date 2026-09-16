using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

internal sealed record GapFreezePathCheckResult(
    bool IsExpected,
    bool ReloadIssued,
    DateTime LastReloadAt,
    string CurrentPath,
    PlaybackResult? Load,
    PlaybackResult? Pause);

internal static class GapFreezePathGuard
{
    public static GapFreezePathCheckResult Check(
        IPlaybackApi playbackApi,
        string? pendingPath,
        double pendingTargetSeconds,
        DateTime lastReloadAt,
        DateTime now,
        TimeSpan reloadDebounce)
    {
        if (string.IsNullOrWhiteSpace(pendingPath))
        {
            return new GapFreezePathCheckResult(
                IsExpected: true,
                ReloadIssued: false,
                lastReloadAt,
                CurrentPath: "",
                Load: null,
                Pause: null);
        }

        string currentPath = playbackApi.GetPath();
        if (ContinueModePlaybackPolicy.IsExpectedMediaPath(currentPath, pendingPath))
        {
            return new GapFreezePathCheckResult(
                IsExpected: true,
                ReloadIssued: false,
                lastReloadAt,
                currentPath,
                Load: null,
                Pause: null);
        }

        if (now - lastReloadAt > reloadDebounce)
        {
            PlaybackResult load = playbackApi.Load(pendingPath, pendingTargetSeconds, paused: true);
            PlaybackResult pause = playbackApi.SetPaused(true);

            return new GapFreezePathCheckResult(
                IsExpected: false,
                ReloadIssued: true,
                now,
                currentPath,
                load,
                pause);
        }

        return new GapFreezePathCheckResult(
            IsExpected: false,
            ReloadIssued: false,
            lastReloadAt,
            currentPath,
            Load: null,
            Pause: null);
    }
}
