namespace TimecodeSyncPlayer;

public interface ISeekBarUpdateState
{
    bool HasPendingSeek { get; }
    double TargetSeconds { get; }
    void MarkSeekSent(double targetSeconds, DateTime sentAt);
    void Clear();
    double GetDisplayPosition(double playerPositionSeconds, DateTime now);
}
