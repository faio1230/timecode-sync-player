namespace TimecodeSyncPlayer;

public interface ITimecodeSyncSeekState
{
    bool HasPendingSeek { get; }
    double TargetSeconds { get; }
    TimecodeSyncSeekPendingStatus LastStatus { get; }
    void BeginSeek(double targetSeconds, DateTime sentAt);
    void Clear();
    bool ShouldSuppressSeek(double playbackSeconds, double toleranceSeconds, DateTime now,
        double requestedTargetSeconds = double.NaN);

    /// <summary>D37-b: 着地までの実測時間（移動平均）。未学習は null。既定実装は未学習。</summary>
    double? LearnedSeekDurationSeconds => null;

    /// <summary>D37-b: 素材が変わったとき（ロード）に学習を捨てる。既定実装は何もしない。</summary>
    void ResetLearning()
    {
    }
}
