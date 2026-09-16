using System.Diagnostics;
using Serilog;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer;

/// <summary>
/// 再生操作と、それに対応する MainWindow の状態更新。
/// ウィンドウ所有の値は呼び出し時に effects 経由で取得する。
/// </summary>
internal sealed class PlaybackOperationsCoordinator
{
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

        _effects.Stop();
        _effects.SetPaused(true);
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

        PlaybackResult load;
        if (startPosition.HasValue)
        {
            load = TracedLoad(() => _effects.Load(path, startPosition, keepPaused),
                (long)Math.Round(startPosition.Value * 1_000_000.0));
            Log.Information("Load path={Path} start={Start:F3} loadOk={LoadOk} error={Error}",
                path, startPosition.Value, load.Success, load.Error);
        }
        else
        {
            load = TracedLoad(() => _effects.Load(path, null, false));
            PlaybackResult pause = _effects.SetPaused(false);
            Log.Information("Load path={Path} start=none loadOk={LoadOk} pauseOk={PauseOk}",
                path, load.Success, pause.Success);
        }

        if (!load.Success) return false;

        // 位置つきロードは EOF pause を引き継ぐことがある。ユーザー／ギャップが持つ pause を
        // 保ったまま、意図した状態を明示し直す。
        if (startPosition.HasValue)
            _effects.SetPaused(keepPaused);
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

        PlaybackResult load = TracedLoad(() => _effects.Load(path, null, true));
        PlaybackResult pause = _effects.SetPaused(true);
        bool success = load.Success;
        Log.Information("Load path={Path} start=none loadOk={LoadOk} pauseOk={PauseOk}",
            path, load.Success, pause.Success);

        if (!success) return false;

        ApplyPauseState(true);
        _effects.ResetPlayerStateForNewTrack();
        _effects.ResetGapFreeze();
        _effects.SetSeekBarValueFromPlayer(0);
        _effects.SetTimeLabel(DefaultTimeLabel);
        return true;
    }

    public bool SeekTo(double seconds)
    {
        // U1 計測: この呼び出しは UI スレッドから同期で shim に入る。
        long started = Stopwatch.GetTimestamp();
        try
        {
            bool trace = OutputTrace.Current.IsEnabled;
            if (trace)
            {
                OutputTrace.Current.Record(new("seek.issue", "PLAYER", Stopwatch.GetTimestamp(),
                    Value: (long)Math.Round(seconds * 1_000_000.0)));
            }
            PlaybackResult seek;
            try
            {
                seek = _effects.Seek(seconds);
            }
            finally
            {
                if (trace)
                    OutputTrace.Current.Record(new("seek.return", "PLAYER", Stopwatch.GetTimestamp()));
            }
            if (!seek.Success)
            {
                Log.Warning("Seek failed: error={Error}, target={Target}", seek.Error, seconds);
                return false;
            }

            // keep-open may pause at EOF without changing the user's play intent.
            _effects.SetPaused(_playbackControl.IsPaused);
            Log.Debug("SeekTo target={Target:F3} ok={Ok} totalMs={TotalMs:F1}",
                seconds, seek.Success, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
    /// 計測専用（出力トレース有効時のみ）。load 発行〜復帰を同じ QPC で残す。
    /// startMicros は新しい開始位置（未指定は -1）。
    /// </summary>
    private PlaybackResult TracedLoad(Func<PlaybackResult> load, long startMicros = -1)
    {
        bool trace = OutputTrace.Current.IsEnabled;
        if (trace)
        {
            OutputTrace.Current.Record(new("load.issue", "PLAYER", Stopwatch.GetTimestamp(),
                Value: startMicros, Detail: startMicros < 0 ? "none" : "start"));
        }
        try
        {
            return load();
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
    Func<string, double?, bool, PlaybackResult> Load,
    Func<double, PlaybackResult> Seek,
    Func<PlaybackResult> Stop,
    Func<bool, PlaybackResult> SetPaused,
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
