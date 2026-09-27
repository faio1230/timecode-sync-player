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
    /// v0.5.4 U4: A（着地の状態）が着地を待っている間（保留中または時間切れ後の再確認中）。
    /// 門 5・10・12 が共有する 1 つの条件。既定の実装は保留の有無。
    /// </summary>
    bool IsWaitingForLanding => HasPendingSeek;

    /// <summary>
    /// v0.5.4 U4: 再生位置を粗い判定・補正に使えるか（= 着地を待っていない。門 10）。
    /// 既定の実装は待っていないことと同じ。
    /// </summary>
    bool IsPositionUsable => !IsWaitingForLanding;

    /// <summary>v0.5.4 U4: 時間切れ後の再確認中か（安定 3 サンプルを数えている間）。既定は false。</summary>
    bool IsReacquiring => false;

    /// <summary>v0.5.4 U4: 位置のサンプルを観測する（再確認中だけ数える）。既定は使えるかを返す。</summary>
    bool ObservePlaybackPosition(double positionSeconds, double nowSeconds) => IsPositionUsable;

    /// <summary>
    /// v0.5.4 U4: 位置の信頼を初期化する（読み込み・手動移動・保留の外部破棄）。既定は何もしない。
    /// </summary>
    void ResetPositionTrust()
    {
    }

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

    /// <summary>v0.5.4 段 B1: 着地の状態（新しい判定）。既定は追従中。</summary>
    TimecodeSyncLandingPhase LandingPhase => TimecodeSyncLandingPhase.Following;

    /// <summary>v0.5.4 段 B1: 新しい世代の最初のフレームが着地の窓の外だった回数。既定は 0。</summary>
    int LandingFirstFrameOutsideWindowCount => 0;

    /// <summary>v0.5.4 段 B1: 新しい判定と古い判定の結論が食い違った回数。既定は 0。</summary>
    int LandingMismatchCount => 0;

    /// <summary>v0.5.4 段 B1: 直近の新しい判定の着地の記録（計測・テスト用）。既定は null。</summary>
    TimecodeSyncLandingRecord? LastLanding => null;

    /// <summary>
    /// v0.5.4 段 B1: 位置サンプルで着地の状態（新しい判定）を観測する。LTC のフレームの経路に
    /// 依らず、位置を照会するすべての場所から呼ぶ。既定実装は何もしない。
    /// </summary>
    void ObserveLandingSample(in PlaybackPositionSample sample, double toleranceSeconds, DateTime now)
    {
    }

    /// <summary>
    /// v0.5.4 段 B1: 着地の状態を初期化する（読み込み・手動移動・保留の外部破棄）。既定実装は何もしない。
    /// </summary>
    void ResetLandingState()
    {
    }
}
