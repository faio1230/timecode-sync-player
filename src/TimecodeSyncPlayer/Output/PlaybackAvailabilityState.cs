namespace TimecodeSyncPlayer.Output;

/// <summary>
/// 再生を行えるかどうかの唯一の判定元（R1 1-2）。GPU 出力の検出失敗、GPU ワーカーの
/// 初期化失敗、プレイヤー生成失敗を 1 つの状態に集約し、再生・シーク・LTC 同期の開始は
/// 必ずこの状態だけを見て止める。散らばった null 判定で代用しない。
/// UI スレッドからのみ更新する。
/// </summary>
public sealed class PlaybackAvailabilityState
{
    public bool IsAvailable { get; private set; } = true;

    /// <summary>利用不可の理由（最初に記録したもの）。ダイアログとログに出す。</summary>
    public string? Detail { get; private set; }

    public void MarkUnavailable(string detail)
    {
        IsAvailable = false;
        Detail ??= detail;
    }
}
