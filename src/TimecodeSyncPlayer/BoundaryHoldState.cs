namespace TimecodeSyncPlayer;

/// <summary>
/// v0.5.2 段 2e: 境界ホールドの状態（位置の持ち主の軸のうち Single の終端ホールド）。
/// 副作用（SetEndHold・解除の通知）とログは <see cref="SingleModeSyncCoordinator"/> に残し、
/// ここは値と遷移だけを持つ。
///
/// 端へのシークの記録は「目標と読み込み番号」の組にしてよい（v0.5.2 段 2e の確認）。
/// 読み込み番号は目標が null のときは読まれない（BoundarySeekSentTo と LatchSnapshot が
/// 短絡する）ため、組が消えても失う読みが無い。
/// </summary>
internal sealed class BoundaryHoldState
{
    /// <summary>端へのシークの記録（目標と、そのときの読み込み番号）。</summary>
    internal readonly record struct BoundarySeek(double Target, long Epoch);

    private BoundarySeek? _seek;

    /// <summary>
    /// D33: 終端ホールド中か。LTC が範囲外で再生位置が clipIn/clipOut に達したら立て、
    /// 許容分だけ内側へ戻ったら解除する。
    /// </summary>
    public bool IsHeld { get; private set; }

    /// <summary>
    /// 端へのシークの記録（null なら無し）。v0.5.1: 別のファイルを読み込んだ後には持ち越さない
    /// （v0.5.0 では持ち越したため、クリップの入口より手前の LTC でホールド中にトラックを
    /// 切り替えると、新しいトラックの位置 0 を「入口に着いた」とみなし、シークせずに頭から
    /// 流していた。検証機の S-4）。
    /// </summary>
    public BoundarySeek? Seek => _seek;

    /// <summary>ホールドを立てる。</summary>
    public void MarkHeld() => IsHeld = true;

    /// <summary>ホールドを解除する。</summary>
    public void ClearHeld() => IsHeld = false;

    /// <summary>
    /// 端へのシーク（範囲外 LTC の着地先）を出したことを覚える。端でなければ記録しない。
    /// 今のコードは目標が null でも読み込み番号を書くが、読み込み番号は目標が無いとき
    /// 読まれないため、組にしない（v0.5.2 段 2e の確認）。
    /// </summary>
    public void NoteSeek(double? target, long epoch)
        => _seek = target is double value ? new BoundarySeek(value, epoch) : null;

    /// <summary>端へのシークの記録を消す。</summary>
    public void ClearSeek() => _seek = null;
}
