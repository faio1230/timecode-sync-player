namespace TimecodeSyncPlayer.Contracts;

/// <summary>0.4.5-A: 位置サンプルの基準（何の時計か）。</summary>
public enum PlaybackPositionBasis
{
    /// <summary>不明（配信フレームがまだ無い等）。</summary>
    None,
    /// <summary>パイプライン位置（gst_element_query_position）。</summary>
    Pipeline,
    /// <summary>最新の配信映像フレームの PTS。</summary>
    Delivered,
}

/// <summary>
/// 0.4.5-A: 1 回のネイティブ照会で同時に取った位置スナップショット。
/// <see cref="Seconds"/> は tcs_player_get_time_pos と同じ値、<see cref="Basis"/> と
/// <see cref="Generation"/> がそれが何の時計か、<see cref="DeliveredSeconds"/> /
/// <see cref="DeliveredGeneration"/> が最新配信フレーム（無ければ 0）、
/// <see cref="CurrentGeneration"/> が同じ瞬間のプレイヤー世代を表す。
/// </summary>
public readonly record struct PlaybackPositionSample(
    double Seconds,
    PlaybackPositionBasis Basis,
    ulong Generation,
    double DeliveredSeconds,
    ulong DeliveredGeneration,
    ulong CurrentGeneration);
