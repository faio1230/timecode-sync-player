namespace TimecodeSyncPlayer;

public interface ITimecodeSyncSeekState
{
    bool HasPendingSeek { get; }
    double TargetSeconds { get; }
    TimecodeSyncSeekPendingStatus LastStatus { get; }
    void BeginSeek(double targetSeconds, DateTime sentAt);
    void Clear();
    bool ShouldSuppressSeek(double playbackSeconds, double toleranceSeconds, DateTime now);
}
