using System.Diagnostics;
using Serilog;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer;

public sealed record GapLoadCommandResult(PlaybackResult Load, PlaybackResult Pause);

public sealed class GapPlaybackCommandExecutor
{
    private readonly IPlaybackApi _playbackApi;

    public GapPlaybackCommandExecutor(IPlaybackApi playbackApi)
    {
        _playbackApi = playbackApi;
    }

    public PlaybackResult PauseForGap() => _playbackApi.SetPaused(true);

    public GapLoadCommandResult LoadPausedAt(string filePath, double targetSeconds)
    {
        // U1 計測: load + pause は UI スレッドから同期で shim に入る。
        long started = Stopwatch.GetTimestamp();
        // 計測専用（出力トレース有効時のみ）。ギャップ経由の load も時系列に載せる。
        bool trace = OutputTrace.Current.IsEnabled;
        if (trace)
        {
            OutputTrace.Current.Record(new("load.issue", "PLAYER", Stopwatch.GetTimestamp(),
                Value: (long)Math.Round(targetSeconds * 1_000_000.0), Detail: "start"));
        }
        PlaybackResult load;
        try
        {
            load = _playbackApi.Load(filePath, targetSeconds, paused: true);
        }
        finally
        {
            if (trace)
                OutputTrace.Current.Record(new("load.return", "PLAYER", Stopwatch.GetTimestamp()));
        }
        PlaybackResult pause = _playbackApi.SetPaused(true);
        Log.Debug("Gap load target={Target:F3} loadOk={LoadOk} pauseOk={PauseOk} totalMs={TotalMs:F1}",
            targetSeconds, load.Success, pause.Success, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new GapLoadCommandResult(load, pause);
    }
}
