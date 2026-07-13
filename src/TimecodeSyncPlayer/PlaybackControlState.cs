namespace TimecodeSyncPlayer;

internal sealed class PlaybackControlState
{
    private static readonly double[] SpeedSteps = [0.5, 1.0, 2.0, 4.0];
    private int _speedIndex = 1;

    public bool IsPaused { get; private set; } = true;

    public PlaybackPauseChange TogglePlayPause() =>
        SetPaused(!IsPaused);

    public PlaybackPauseChange SetPaused(bool paused)
    {
        IsPaused = paused;
        return new PlaybackPauseChange(IsPaused, IsPaused ? "yes" : "no", IsPaused ? "▶" : "⏸");
    }

    public PlaybackSpeedChange CycleSpeed()
    {
        _speedIndex = (_speedIndex + 1) % SpeedSteps.Length;
        return CreateSpeedChange(SpeedSteps[_speedIndex]);
    }

    public PlaybackSpeedChange ResetSpeed()
    {
        _speedIndex = 1;
        return CreateSpeedChange(SpeedSteps[_speedIndex]);
    }

    private static PlaybackSpeedChange CreateSpeedChange(double speed) =>
        new(speed, speed == 1.0 ? "1×" : $"{speed}×");
}

internal sealed record PlaybackPauseChange(bool IsPaused, string MpvPauseValue, string PlayPauseIcon);

internal sealed record PlaybackSpeedChange(double Speed, string Label);
