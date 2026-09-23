using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

/// <summary>
/// v0.5.1: 同期評価用の再生位置の取得結果。<see cref="PlaybackSeconds"/> と
/// <see cref="Sample"/> は同じ 1 回の照会から作る（別々に取り直さない）。
/// <see cref="Sample"/> は位置サンプル（_ex）が取れなかったとき null。
/// <see cref="Succeeded"/> が false のとき、ほかの値は使わない。
/// </summary>
internal readonly record struct SyncPositionRead(
    bool Succeeded,
    double PlaybackSeconds,
    PlaybackPositionSample? Sample = null)
{
    /// <summary>位置を取得できなかった結果。</summary>
    public static SyncPositionRead Failed => new(false, 0.0);
}
