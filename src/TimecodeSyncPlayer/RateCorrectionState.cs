namespace TimecodeSyncPlayer;

/// <summary>
/// v0.5.2 段 2d: 速度補正の軸の状態（適用中の倍率・1.0 への戻し待ち・Smooth 使用可否・
/// 位置不安定での停止・補正の残差ゲート）。副作用（rate.instant・シーク・状態表示）とログは
/// <see cref="LtcSyncController"/> に残し、ここは値と遷移だけを持つ。
///
/// 倍率と戻し待ちは 1 つの段階で表す（v0.5.2 段 2d の確認）。「戻し待ちなら倍率は 1.0 から
/// 0.0005 以上離れている」がすべての書き込みの後で成り立つため、次の 3 段階で足りる:
/// Unity（倍率 1.0・戻し待ちなし）／Applied(rate)／RestorePending(rate)。
/// 段階のデータに倍率をそのまま持つ（1.0 の近傍でも値を丸めない。SetRate の変化判定が
/// 丸めで変わらないようにする）。
/// </summary>
internal sealed class RateCorrectionState
{
    private const double UnityTolerance = 0.0005;

    private enum Stage
    {
        Unity,
        Applied,
        RestorePending
    }

    private readonly SeekDecisionGate _residualGate = new();
    private Stage _stage = Stage.Unity;
    private double _stageRate = 1.0;
    private bool _rejectedLogged;

    /// <summary>T5: Smooth の使用可否（レート変更が拒否されたら false。有効化・切替で再試行）。</summary>
    public bool SmoothAvailable { get; private set; } = true;

    /// <summary>0.4.8: 位置が不安定で速度補正を止めている間 true（開始と終了を 1 回ずつログに残す）。</summary>
    public bool CorrectionPausedForPosition { get; private set; }

    /// <summary>1.0 への戻し待ちか。</summary>
    public bool RateRestorePending => _stage == Stage.RestorePending;

    /// <summary>D20-b: 同期へ実際に適用した最後の値（保持値の変更判定に使う）。</summary>
    public double LastAppliedRate => _stage == Stage.Unity ? 1.0 : _stageRate;

    /// <summary>倍率が 1.0 でないまま残っているか（ResetCorrection と同じ判定幅 0.0005）。</summary>
    public bool RateNotUnity => Math.Abs(LastAppliedRate - 1.0) >= UnityTolerance;

    /// <summary>D37-c: 補正の残差ゲートで弾いた標本の累計。</summary>
    public long RejectedSamples => _residualGate.RejectedSamples;

    /// <summary>D37-c: 弾いたことを直近にログへ出したか。</summary>
    public bool RejectedLogged => _rejectedLogged;

    /// <summary>倍率を適用した（1.0 なら Unity、それ以外は Applied に移る）。</summary>
    public void MarkRateApplied(double rate)
    {
        _stageRate = rate;
        _stage = rate == 1.0 ? Stage.Unity : Stage.Applied;
    }

    /// <summary>倍率を 1.0 へ戻した（戻し待ちも解消）。</summary>
    public void MarkRestored()
    {
        _stageRate = 1.0;
        _stage = Stage.Unity;
    }

    /// <summary>
    /// 1.0 への戻しに失敗した（戻し待ちにする）。今の呼び出し元は「倍率が 1.0 から
    /// 0.0005 以上離れている」ときだけ呼ぶ（戻し待ちの間の不変条件）。
    /// </summary>
    public void MarkRestorePending() => _stage = Stage.RestorePending;

    /// <summary>T5: レート変更が拒否され、Smooth を使えないと分かった。</summary>
    public void MarkSmoothUnavailable() => SmoothAvailable = false;

    /// <summary>T5/T7: Smooth を再試行できるようにする（有効化・モード切替・トラック切替）。</summary>
    public void ResetSmoothAvailability() => SmoothAvailable = true;

    /// <summary>0.4.8: 位置が不安定になり、補正を止めに入る。今回 false→true に変わったときだけ true。</summary>
    public bool EnterPositionPause()
    {
        if (CorrectionPausedForPosition)
            return false;
        CorrectionPausedForPosition = true;
        return true;
    }

    /// <summary>0.4.8: 位置が安定し、補正を再開する。今回 true→false に変わったときだけ true。</summary>
    public bool ExitPositionPause()
    {
        if (!CorrectionPausedForPosition)
            return false;
        CorrectionPausedForPosition = false;
        return true;
    }

    /// <summary>D37-c: 補正の残差の系列を切る（前の系列の中央値・変化量を混ぜない）。</summary>
    public void ResetResidualGate() => _residualGate.Reset();

    /// <summary>D37-c: 補正の残差を 1 サンプル観測する。</summary>
    public SeekDecisionGate.Result ObserveResidual(
        double residualSeconds, double toleranceSeconds, double nowSeconds, double granularitySeconds) =>
        _residualGate.Observe(residualSeconds, toleranceSeconds, nowSeconds, granularitySeconds);

    /// <summary>D37-c: 弾いたことをログへ出す（乱れの切れ目に 1 回だけ）。</summary>
    public void MarkRejectedLogged() => _rejectedLogged = true;

    /// <summary>D37-c: 弾いていないサンプルで、次の乱れに備えてログのラッチを下ろす。</summary>
    public void ClearRejectedLogged() => _rejectedLogged = false;
}
