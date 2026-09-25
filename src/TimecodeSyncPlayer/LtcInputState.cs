namespace TimecodeSyncPlayer;

/// <summary>
/// v0.5.2 段 2b: 入力（LTC）の軸の状態。LtcSyncController にあった 15 個のフィールドをここへ移した。
/// 書き換えはメソッドに集め、読み取りはプロパティにする。各メソッドが触る値と順番は、移す前の
/// 代入そのまま（振る舞いは段 2b の前と同じ）。
/// </summary>
internal sealed class LtcInputState
{
    /// <summary>最後に受理したフレーム（同期に使う実効値・生値・フレーム終端）。</summary>
    internal readonly record struct AcceptedFrame(double EffectiveSeconds, double RawSeconds, long FrameEndTimestamp);

    /// <summary>同期要求の再送用の保留（評価が Deferred のときだけ持つ）。</summary>
    internal readonly record struct PendingSync(double EffectiveSeconds, double RawSeconds, long FrameEndTimestamp);

    /// <summary>最後に受理したフレーム。null なら未受理。</summary>
    public AcceptedFrame? Accepted { get; private set; }

    /// <summary>同期要求の再送用の保留。null なら保留なし。</summary>
    public PendingSync? Pending { get; private set; }

    // PendingJump は 3 つ組の record にしない（v0.5.2 段 2b の確認）。今のコードは
    // 未確認 Jump の確認で _pendingJumpSeconds を null にした後、_pendingJumpFrameEndTimestamp と
    // _pendingJumpReceivedAt を読む（LtcSyncController の受信経路）。まとめると null と同時に
    // 残り 2 つが消え、その読みが変わるため、3 つを別々に持つ。

    /// <summary>
    /// D30: 未確認の Jump。写像がギャップ／別トラック、または Fixed モードでデコーダ推定 fps が
    /// 食い違う Jump を保持し、次の 1 フレームの連続（同値 Duplicate か +1 フレーム）で確認して
    /// から適用する。誤デコード 1 枚でギャップ進入・トラック切替・保持復帰を起こさない。
    /// </summary>
    public double? PendingJumpSeconds { get; private set; }

    /// <summary>未確認 Jump を受けた時刻（ミリ秒）。確認の窓の壁時計側に使う。</summary>
    public long PendingJumpReceivedAt { get; private set; }

    /// <summary>未確認 Jump のフレーム終端（サンプル時計）。確認の窓の優先側に使う。</summary>
    public long PendingJumpFrameEndTimestamp { get; private set; }

    /// <summary>D20-b: 同期へ実際に適用した最後の値（保持値の変更判定に使う）。</summary>
    public double? LastAppliedLtcSeconds { get; private set; }

    /// <summary>
    /// D27-d: 保持（Duplicate）として届いた最後の値。停止時の着地目標は保持値そのものにし、
    /// 保持直前の受理値（1 フレーム手前になり得る）を使わない。Normal/Initial で解除する。
    /// </summary>
    public double? LastHeldEffectiveSeconds { get; private set; }

    /// <summary>
    /// D31-b: 保持損失中に着地シークを発行した保持値。この値から保持値が変わったら 1 回だけ
    /// 新しい保持値へ着地する（同じ保持値の連続では発行しない）。損失が明けたら解除する。
    /// </summary>
    public double? HeldLossLandingSeconds { get; private set; }

    /// <summary>D20-b (i): 同一の Jump 連続で何度も適用しないためのラッチ（Normal/Initial で解除）。</summary>
    public bool JumpAppliedOnce { get; private set; }

    /// <summary>D20-b: 保持値の変更で 1 回だけ適用したことを示すラッチ（Normal/Initial で解除）。</summary>
    public bool HeldReapplyDone { get; private set; }

    /// <summary>
    /// D37-c: 追従開始（同期の有効化・監視開始）の最初の同期評価を、既存の着地窓
    /// （D37-b2 の NotifyLanding / RateCatchUpAllowed）と同じ扱いにする。追従開始の瞬間は
    /// 画面がまだ合っていないので、速度補正より速いシークで詰める。
    /// </summary>
    public bool FollowStartPending { get; private set; }

    /// <summary>監視の開始・停止で、受けたフレームの記録と 1 回適用のラッチを捨てる（移す前の ClearFrameHistory）。</summary>
    public void ClearFrameHistory()
    {
        Accepted = null;
        LastAppliedLtcSeconds = null;
        LastHeldEffectiveSeconds = null;
        HeldLossLandingSeconds = null;
        Pending = null;
        DiscardPendingJump();
        JumpAppliedOnce = false;
        HeldReapplyDone = false;
    }

    /// <summary>未確認 Jump の秒とフレーム終端を捨てる（受信時刻は今のコードと同じく残す）。</summary>
    public void DiscardPendingJump()
    {
        PendingJumpSeconds = null;
        PendingJumpFrameEndTimestamp = 0;
    }

    /// <summary>未確認 Jump の秒だけを下ろす（確認の分岐。フレーム終端と受信時刻は読むために残す）。</summary>
    public void ClearPendingJumpSeconds() => PendingJumpSeconds = null;

    /// <summary>未確認 Jump を 3 つとも立てる。</summary>
    public void HoldPendingJump(double seconds, long receivedAt, long frameEndTimestamp)
    {
        PendingJumpSeconds = seconds;
        PendingJumpReceivedAt = receivedAt;
        PendingJumpFrameEndTimestamp = frameEndTimestamp;
    }

    /// <summary>同期要求の再送用の保留を捨てる。</summary>
    public void DiscardPendingSync() => Pending = null;

    /// <summary>同期要求の再送用の保留を立てる（評価が Deferred のとき）。</summary>
    public void HoldPendingSync(double effectiveSeconds, double rawSeconds, long frameEndTimestamp)
        => Pending = new PendingSync(effectiveSeconds, rawSeconds, frameEndTimestamp);

    /// <summary>保持（Duplicate）として届いた最後の値を覚える。</summary>
    public void MarkHeldEffective(double seconds) => LastHeldEffectiveSeconds = seconds;

    /// <summary>保持（Duplicate）の値を捨てる（値が進むフレームで保持が明けたとき）。</summary>
    public void ClearHeldEffective() => LastHeldEffectiveSeconds = null;

    /// <summary>Jump を 1 回適用したラッチを立てる。</summary>
    public void MarkJumpApplied() => JumpAppliedOnce = true;

    /// <summary>Jump を 1 回適用したラッチを下ろす。</summary>
    public void ClearJumpApplied() => JumpAppliedOnce = false;

    /// <summary>保持値の 1 回適用のラッチを立てる。</summary>
    public void MarkHeldReapplied() => HeldReapplyDone = true;

    /// <summary>保持値の 1 回適用のラッチを下ろす。</summary>
    public void ClearHeldReapplied() => HeldReapplyDone = false;

    /// <summary>値が進むフレームで、1 回適用のラッチ 2 つと保持値 2 つを下ろす。</summary>
    public void OnNormalFrame()
    {
        JumpAppliedOnce = false;
        HeldReapplyDone = false;
        LastHeldEffectiveSeconds = null;
        HeldLossLandingSeconds = null;
    }

    /// <summary>損失中の保持着地のラッチを下ろす。</summary>
    public void ClearHeldLossLanding() => HeldLossLandingSeconds = null;

    /// <summary>損失中の保持着地のラッチを立てる（着地を試みた保持値）。</summary>
    public void MarkHeldLossLanding(double seconds) => HeldLossLandingSeconds = seconds;

    /// <summary>最後に受理したフレームを覚え、同期へ適用した最後の値も同じ実効値にする。</summary>
    public void AcceptFrame(double effectiveSeconds, double rawSeconds, long frameEndTimestamp)
    {
        Accepted = new AcceptedFrame(effectiveSeconds, rawSeconds, frameEndTimestamp);
        LastAppliedLtcSeconds = effectiveSeconds;
    }

    /// <summary>同期へ適用した最後の値だけを書く（保持値からの適用・ロード解除の再適用）。</summary>
    public void MarkLastApplied(double? seconds) => LastAppliedLtcSeconds = seconds;

    /// <summary>追従開始の消費待ちを立てる（同期の有効化・監視開始）。</summary>
    public void MarkFollowStart() => FollowStartPending = true;

    /// <summary>追従開始の消費待ちを下ろす（同期の無効化・監視停止・ApplySync での消費）。</summary>
    public void ClearFollowStart() => FollowStartPending = false;
}
