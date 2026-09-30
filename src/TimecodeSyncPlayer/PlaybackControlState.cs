namespace TimecodeSyncPlayer;

internal sealed class PlaybackControlState
{
    private static readonly double[] SpeedSteps = [0.5, 1.0, 2.0, 4.0];
    private int _speedIndex = 1;

    public bool IsPaused { get; private set; } = true;

    /// <summary>
    /// v0.5.4 K5（§6 の 15）: 利用者が自分の操作（再生ボタン）で止めている。ほかの持ち主の
    /// 一時停止・再開では変わらず、利用者が再開するか、読み込み・停止で下ろす。
    /// </summary>
    public bool UserPauseOwned { get; private set; }

    public PlaybackPauseChange TogglePlayPause()
    {
        PlaybackPauseChange change = SetPaused(!IsPaused);
        // v0.5.4 K5（§6 の 15）: 利用者の操作の結果だけを「利用者が止めている」とする。
        UserPauseOwned = change.IsPaused;
        return change;
    }

    /// <summary>
    /// v0.5.4 K5（§6 の 15）: 利用者以外の理由の読み込み・停止で、利用者の一時停止の主張を下ろす
    /// （新しいファイルは止めるかどうかをロードが決める。古い主張を残さない）。
    /// </summary>
    public void ClearUserPauseOwned() => UserPauseOwned = false;

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

internal sealed record PlaybackPauseChange(bool IsPaused, string PauseValue, string PlayPauseIcon);

internal sealed record PlaybackSpeedChange(double Speed, string Label);
