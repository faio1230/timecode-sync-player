using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

internal sealed record GapFreezePathCheckResult(
    bool IsExpected,
    bool ReloadIssued,
    DateTime LastReloadAt,
    string CurrentPath,
    int? LoadRc,
    int? PauseRc);

internal static class GapFreezePathGuard
{
    public static GapFreezePathCheckResult Check(
        IMpvApi mpvApi,
        IntPtr mpv,
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
                LoadRc: null,
                PauseRc: null);
        }

        string currentPath = mpvApi.GetPropertyString(mpv, "path");
        if (ContinueModePlaybackPolicy.IsExpectedMediaPath(currentPath, pendingPath))
        {
            return new GapFreezePathCheckResult(
                IsExpected: true,
                ReloadIssued: false,
                lastReloadAt,
                currentPath,
                LoadRc: null,
                PauseRc: null);
        }

        if (now - lastReloadAt > reloadDebounce)
        {
            int loadRc = mpvApi.CommandString(
                mpv,
                MpvPlaybackCommandBuilder.BuildLoadFileCommand(pendingPath, pendingTargetSeconds));
            int pauseRc = mpvApi.SetPropertyString(mpv, "pause", "yes");

            return new GapFreezePathCheckResult(
                IsExpected: false,
                ReloadIssued: true,
                now,
                currentPath,
                loadRc,
                pauseRc);
        }

        return new GapFreezePathCheckResult(
            IsExpected: false,
            ReloadIssued: false,
            lastReloadAt,
            currentPath,
            LoadRc: null,
            PauseRc: null);
    }
}
