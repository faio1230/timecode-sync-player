using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

public sealed record GapPlaybackCommandResult(int PauseRc, int OsdBarRc);
public sealed record GapLoadCommandResult(int LoadRc, int PauseRc);

public sealed class GapPlaybackCommandExecutor
{
    private readonly IMpvApi _mpvApi;

    public GapPlaybackCommandExecutor(IMpvApi mpvApi)
    {
        _mpvApi = mpvApi;
    }

    public GapPlaybackCommandResult PauseForGap(IntPtr mpv)
    {
        int pauseRc = _mpvApi.SetPropertyString(mpv, "pause", "yes");
        int osdRc = _mpvApi.SetPropertyString(mpv, "osd-bar", "no");
        return new GapPlaybackCommandResult(pauseRc, osdRc);
    }

    public GapLoadCommandResult LoadPausedAt(IntPtr mpv, string filePath, double targetSeconds)
    {
        int loadRc = _mpvApi.CommandString(mpv, MpvPlaybackCommandBuilder.BuildLoadFileCommand(filePath, targetSeconds));
        int pauseRc = _mpvApi.SetPropertyString(mpv, "pause", "yes");
        return new GapLoadCommandResult(loadRc, pauseRc);
    }
}
