namespace TimecodeSyncPlayer.Contracts;

internal interface IPlaybackController
{
    void TogglePlayPause();
    void SeekRelative(double seconds);
    /// <summary>v0.6.6 R-4: 今の表示のフレームから steps フレーム（+ で送る、- で戻す）。</summary>
    void StepFrame(int steps);
    void CycleSpeed();
}
