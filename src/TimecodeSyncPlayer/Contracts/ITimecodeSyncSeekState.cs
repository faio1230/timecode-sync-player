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

    /// <summary>
    /// D38 (b): 未信頼のフレームで、要求が pending の目標からも現在位置からも離れているとき、
    /// 到達不能な pending を捨てる。既定実装は捨てない（false）。
    /// </summary>
    bool DiscardIfUnreachable(
        double requestedTargetSeconds, double toleranceSeconds, double playbackSeconds) => false;

    /// <summary>D37-b: 着地までの実測時間（移動平均）。未学習は null。既定実装は未学習。</summary>
    double? LearnedSeekDurationSeconds => null;

    /// <summary>D37-b: 素材が変わったとき（ロード）に学習を捨てる。既定実装は何もしない。</summary>
    void ResetLearning()
    {
    }

    /// <summary>
    /// v0.5.3 段 3f: 直前の着地の記録（着地後の 0.5 秒の抑止に使う）だけを忘れる。読み込みで
    /// 素材が変わるため、前のファイルの着地目標を新しいファイルへ持ち越さない。既定実装は何もしない。
    /// </summary>
    void ForgetLastSettled()
    {
    }
}
