using System.Globalization;
using Serilog;

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
        _effects.ResetVideoWidth();
        _effects.ResetVideoHeight();
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

        bool success;
        if (startPosition.HasValue)
        {
            int loadRc = _effects.CommandString(
                MpvPlaybackCommandBuilder.BuildLoadFileCommand(path, startPosition));
            success = loadRc == 0;
            Log.Information("LoadFile path={Path} start={Start:F3} loadRc={LoadRc}",
                path, startPosition.Value, loadRc);
        }
        else
        {
            int loadRc = _effects.CommandString(
                MpvPlaybackCommandBuilder.BuildLoadFileCommand(path, startPosition: null));
            int pauseRc = _effects.SetPropertyString("pause", MpvValueNo);
            success = loadRc == 0;
            Log.Information("LoadFile path={Path} start=none loadRc={LoadRc} pauseRc={PauseRc}",
                path, loadRc, pauseRc);
        }

        if (!success) return false;

        ApplyPauseState(false);
        _effects.ResetPlayerStateForNewTrack();
        _effects.ResetVideoWidth();
        _effects.ResetVideoHeight();
        _effects.ResetGapFreeze();
        _effects.SetSeekBarValueFromPlayer(0);
        _effects.SetTimeLabel(DefaultTimeLabel);
        return true;
    }

    public bool SeekTo(double seconds, bool suppressOsd = true)
    {
        try
        {
            var prefix = suppressOsd ? MpvCommandNoOsd : "";
            var command = $"{prefix} seek {seconds.ToString("F3", CultureInfo.InvariantCulture)} {MpvSeekModeAbsolute}".Trim();
            int rc = _effects.CommandString(command);
            if (rc != 0)
            {
                Log.Warning("Seek failed: rc={Rc}, target={Target}", rc, seconds);
                return false;
            }

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
}

internal sealed record PlaybackOperationsEffects(
    Func<bool> IsMpvReady,
    Func<string, int> CommandString,
    Func<string, string, int> SetPropertyString,
    Action ResetPlayerStateForNewTrack,
    Action ResetVideoWidth,
    Action ResetVideoHeight,
    Action ClearLoadedTrackId,
    Func<bool> HasTimelinePanel,
    Action ClearTimelineLoadedTrackId,
    Action<double> SetSeekBarValueFromPlayer,
    Action<string> SetTimeLabel,
    Action<string> SetPlayPauseIcon,
    Action ResetGapFreezeAll,
    Action ResetGapFreeze,
    Action ClearGapFreezeFrame);
