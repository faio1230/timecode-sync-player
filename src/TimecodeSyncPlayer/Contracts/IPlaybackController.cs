namespace TimecodeSyncPlayer.Contracts;

internal interface IPlaybackController
{
    void TogglePlayPause();
    void SeekRelative(double seconds);
    void CycleSpeed();
}
