using System.Diagnostics;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;

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
        // 計測専用（出力トレース有効時のみ）。ギャップ経由の loadfile も時系列に載せる。
        bool trace = OutputTrace.Current.IsEnabled;
        if (trace)
        {
            OutputTrace.Current.Record(new("load.issue", "PLAYER", Stopwatch.GetTimestamp(),
                Value: (long)Math.Round(targetSeconds * 1_000_000.0), Detail: "start"));
        }
        int loadRc;
        try
        {
            loadRc = _mpvApi.CommandString(mpv, MpvPlaybackCommandBuilder.BuildLoadFileCommand(filePath, targetSeconds));
        }
        finally
        {
            if (trace)
                OutputTrace.Current.Record(new("load.return", "PLAYER", Stopwatch.GetTimestamp()));
        }
        int pauseRc = _mpvApi.SetPropertyString(mpv, "pause", "yes");
        return new GapLoadCommandResult(loadRc, pauseRc);
    }
}
