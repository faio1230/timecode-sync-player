using System.Diagnostics;
using System.Globalization;
using Serilog;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer;

/// <summary>
/// mpv playback commands and the corresponding MainWindow state updates.
/// Every window-owned value is accessed through effects at invocation time.
/// </summary>
internal sealed class PlaybackOperationsCoordinator
{
    private const string MpvSeekModeAbsolute = "absolute+exact";
    private const string MpvCommandNoOsd = "no-osd";
    private const string MpvCommandStop = "stop";
    private const string MpvValueYes = "yes";
    private const string MpvValueNo = "no";
    private const string DefaultTimeLabel = "0:00 / 0:00";
    private const string IconPlay = "▶";

    private readonly PlaybackControlState _playbackControl;
    private readonly PlaybackOperationsEffects _effects;

    public PlaybackOperationsCoordinator(
        PlaybackControlState playbackControl,
        PlaybackOperationsEffects effects)
    {
        _playbackControl = playbackControl;
        _effects = effects;
    }

    public void StopPlayback()
    {
        if (!_effects.IsMpvReady()) return;

        _effects.CommandString(MpvCommandStop);
        _effects.SetPropertyString("pause", MpvValueYes);
        ApplyPauseState(true);
        _effects.ResetPlayerStateForNewTrack();
        _effects.ClearLoadedTrackId();
        if (_effects.HasTimelinePanel())
            _effects.ClearTimelineLoadedTrackId();
        _effects.SetSeekBarValueFromPlayer(0);
        _effects.SetTimeLabel(DefaultTimeLabel);
        _effects.SetPlayPauseIcon(IconPlay);
        _effects.ResetGapFreezeAll();
        _effects.ClearGapFreezeFrame();
    }

    public bool LoadFile(string path, double? startPosition = null)
    {
        if (!_effects.IsMpvReady()) return false;
        bool keepPaused = startPosition.HasValue && _playbackControl.IsPaused;

        bool success;
        if (startPosition.HasValue)
        {
            int loadRc = TracedLoad(
                MpvPlaybackCommandBuilder.BuildLoadFileCommand(path, startPosition),
                (long)Math.Round(startPosition.Value * 1_000_000.0));
            success = loadRc == 0;
            Log.Information("LoadFile path={Path} start={Start:F3} loadRc={LoadRc}",
                path, startPosition.Value, loadRc);
        }
        else
        {
            int loadRc = TracedLoad(
                MpvPlaybackCommandBuilder.BuildLoadFileCommand(path, startPosition: null));
            int pauseRc = _effects.SetPropertyString("pause", MpvValueNo);
            success = loadRc == 0;
            Log.Information("LoadFile path={Path} start=none loadRc={LoadRc} pauseRc={PauseRc}",
                path, loadRc, pauseRc);
        }

        if (!success) return false;

        // A positioned load can inherit mpv's EOF pause. Apply the intended state
        // explicitly, while preserving a pause owned by the user or gap policy.
        if (startPosition.HasValue)
            _effects.SetPropertyString("pause", keepPaused ? MpvValueYes : MpvValueNo);
        ApplyPauseState(keepPaused);
        _effects.ResetPlayerStateForNewTrack();
        _effects.ResetGapFreeze();
        _effects.SetSeekBarValueFromPlayer(0);
        _effects.SetTimeLabel(DefaultTimeLabel);
        return true;
    }

    public bool LoadFilePaused(string path)
    {
        if (!_effects.IsMpvReady()) return false;

        int loadRc = TracedLoad(MpvPlaybackCommandBuilder.BuildLoadFileCommand(path, startPosition: null));
        int pauseRc = _effects.SetPropertyString("pause", MpvValueYes);
        bool success = loadRc == 0;
        Log.Information("LoadFile path={Path} start=none loadRc={LoadRc} pauseRc={PauseRc}",
            path, loadRc, pauseRc);

        if (!success) return false;

        ApplyPauseState(true);
        _effects.ResetPlayerStateForNewTrack();
        _effects.ResetGapFreeze();
        _effects.SetSeekBarValueFromPlayer(0);
        _effects.SetTimeLabel(DefaultTimeLabel);
        return true;
    }

    public bool SeekTo(double seconds, bool suppressOsd = true)
    {
        // U1 計測: この呼び出しは UI スレッドから同期で mpv/shim に入る。
        long started = Stopwatch.GetTimestamp();
        try
        {
            var prefix = suppressOsd ? MpvCommandNoOsd : "";
            var command = $"{prefix} seek {seconds.ToString("F3", CultureInfo.InvariantCulture)} {MpvSeekModeAbsolute}".Trim();
            // 計測専用（出力トレース有効時のみ）。プレイヤーへの seek 発行〜復帰を同じ QPC で残す。
            // GStreamer ではこの呼び出しが shim の tcs_player_seek を同期で通る。
            bool trace = OutputTrace.Current.IsEnabled;
            if (trace)
            {
                OutputTrace.Current.Record(new("seek.issue", "PLAYER", Stopwatch.GetTimestamp(),
                    Value: (long)Math.Round(seconds * 1_000_000.0),
                    Detail: suppressOsd ? "no-osd" : "osd"));
            }
            int rc;
            try
            {
                rc = _effects.CommandString(command);
            }
            finally
            {
                if (trace)
                    OutputTrace.Current.Record(new("seek.return", "PLAYER", Stopwatch.GetTimestamp()));
            }
            if (rc != 0)
            {
                Log.Warning("Seek failed: rc={Rc}, target={Target}", rc, seconds);
                return false;
            }

            // keep-open may pause mpv at EOF without changing the user's play intent.
            _effects.SetPropertyString("pause", _playbackControl.IsPaused ? MpvValueYes : MpvValueNo);
            Log.Debug("SeekTo target={Target:F3} rc={Rc} totalMs={TotalMs:F1}",
                seconds, rc, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Seek error: target={Target}", seconds);
            return false;
        }
    }

    public void ApplyPauseState(bool paused)
    {
        PlaybackPauseChange change = _playbackControl.SetPaused(paused);
        _effects.SetPlayPauseIcon(change.PlayPauseIcon);
    }

    /// <summary>
    /// 計測専用（出力トレース有効時のみ）。loadfile 発行〜復帰を同じ QPC で残す。
    /// startMicros は新しい開始位置（未指定は -1）。
    /// </summary>
    private int TracedLoad(string command, long startMicros = -1)
    {
        bool trace = OutputTrace.Current.IsEnabled;
        if (trace)
        {
            OutputTrace.Current.Record(new("load.issue", "PLAYER", Stopwatch.GetTimestamp(),
                Value: startMicros, Detail: startMicros < 0 ? "none" : "start"));
        }
        try
        {
            return _effects.CommandString(command);
        }
        finally
        {
            if (trace)
                OutputTrace.Current.Record(new("load.return", "PLAYER", Stopwatch.GetTimestamp()));
        }
    }
}

internal sealed record PlaybackOperationsEffects(
    Func<bool> IsMpvReady,
    Func<string, int> CommandString,
    Func<string, string, int> SetPropertyString,
    Action ResetPlayerStateForNewTrack,
    Action ClearLoadedTrackId,
    Func<bool> HasTimelinePanel,
    Action ClearTimelineLoadedTrackId,
    Action<double> SetSeekBarValueFromPlayer,
    Action<string> SetTimeLabel,
    Action<string> SetPlayPauseIcon,
    Action ResetGapFreezeAll,
    Action ResetGapFreeze,
    Action ClearGapFreezeFrame);
