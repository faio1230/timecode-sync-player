using TimecodeSyncPlayer.Contracts;

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
    /// v0.5.4 段 B: A（着地の状態）が着地を待っている間。門 5・10・12 が共有する 1 つの条件。
    /// 既定の実装は保留の有無。
    /// </summary>
    bool IsWaitingForLanding => HasPendingSeek;

    /// <summary>
    /// v0.5.4 段 B: 再生位置を粗い判定・補正に使えるか（= 着地を待っていない。門 10）。
    /// 既定の実装は待っていないことと同じ。
    /// </summary>
    bool IsPositionUsable => !IsWaitingForLanding;

    /// <summary>v0.5.4 段 B: 遠い新要求で着地待ちの目標を置き換え、そのシークを待っているか。既定は false。</summary>
    bool HasPendingReplacement => false;

    /// <summary>
    /// v0.5.4 段 B / 門 8: 要求が着地待ちの目標からも現在位置からも離れているとき、着地待ちの目標を
    /// 置き換える（位置は使わないまま、置き換えのシークをその場で出す）。既定実装は置き換えない。
    /// </summary>
    bool ReplaceWaitTarget(
        double requestedTargetSeconds, double toleranceSeconds, double playbackSeconds, DateTime now) => false;

    /// <summary>D37-b: 着地までの実測時間（移動平均）。未学習は null。既定実装は未学習。</summary>
    double? LearnedSeekDurationSeconds => null;

    /// <summary>D37-b: 素材が変わったとき（ロード）に学習を捨てる。既定実装は何もしない。</summary>
    void ResetLearning()
    {
    }

    /// <summary>v0.5.4 段 B: 着地の状態（追従中／着地待ち／着地せず）。既定は追従中。</summary>
    TimecodeSyncLandingPhase LandingPhase => TimecodeSyncLandingPhase.Following;

    /// <summary>v0.5.4 段 B: 新しい世代の最初のフレームが着地の窓の外だった回数。既定は 0。</summary>
    int LandingFirstFrameOutsideWindowCount => 0;

    /// <summary>v0.5.4 段 B: 直近の着地の記録（計測・テスト用）。既定は null。</summary>
    TimecodeSyncLandingRecord? LastLanding => null;

    /// <summary>
    /// v0.5.4 段 B: 位置サンプルで着地の状態を観測する。LTC のフレームの経路に依らず、位置を
    /// 照会するすべての場所から呼ぶ。既定実装は何もしない。
    /// </summary>
    void ObserveLandingSample(in PlaybackPositionSample sample, double toleranceSeconds, DateTime now)
    {
    }

    /// <summary>
    /// v0.5.4 段 B: 着地の状態を初期化する（読み込み・手動移動・保留の外部破棄）。既定実装は何もしない。
    /// </summary>
    void ResetLandingState()
    {
    }

    /// <summary>
    /// v0.5.4 段 B: 開始位置つきの読み込みの着地待ちに入る（読み込みの世代の最初のフレームの配信で
    /// 着地する）。既定実装は何もしない。
    /// </summary>
    void BeginLoadWait(DateTime now)
    {
    }

    /// <summary>
    /// v0.5.4 段 B2 の計測: 着地から 500ms 以内に出た同期シークを数える。既定実装は何もしない。
    /// </summary>
    void NotePostLandingSeekIssued(double targetSeconds, DateTime now)
    {
    }

    /// <summary>
    /// v0.5.4 段 B2 の計測: 着地から 500ms 以内に出た速度補正（rate.instant）を数える。既定実装は何もしない。
    /// </summary>
    void NotePostLandingRateApplied(double rate, DateTime now)
    {
    }
}
