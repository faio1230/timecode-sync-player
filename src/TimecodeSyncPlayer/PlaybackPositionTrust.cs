namespace TimecodeSyncPlayer;

/// <summary>
/// D37-b: シーク中と着地直後の再生位置は信用しない。
/// 実測で、連鎖中の再生位置の問い合わせは理想直線から σ=445ms（-781〜+567ms）振れる一方、
/// LTC 側は σ=0.3ms で安定していた。位置を使う判定（粗い同期と補正）は、
/// 保留中は評価せず、時間切れで解除した後は「位置の進みが実時間の進みと整合する」サンプルが
/// 続くまで再開しない（着地が確認できた通常のセットルは、その場で再開してよい）。
/// </summary>
internal sealed class PlaybackPositionTrust
{
    /// <summary>位置の増分を許す割合。実時間 Δt に対し |Δpos − Δt| ≤ 0.20 × Δt（実測 ±20% のレート上限）。</summary>
    public const double RateToleranceRatio = 0.20;

    /// <summary>時間切れ解除後に、判定を再開するために必要な連続サンプル数。</summary>
    public const int RequiredStableSamples = 3;

    private readonly int _requiredSamples;
    private double _lastPositionSeconds = double.NaN;
    private double _lastAtSeconds = double.NaN;
    private int _stableSamples;

    private bool _trusted = true;
    private bool _reacquiring;

    public PlaybackPositionTrust(int requiredStableSamples = RequiredStableSamples)
    {
        if (requiredStableSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(requiredStableSamples));
        _requiredSamples = requiredStableSamples;
    }

    /// <summary>いま位置を判定に使えるか。</summary>
    public bool IsTrusted => _trusted;

    /// <summary>時間切れ後に安定サンプルを数えている最中か（この間だけ Observe する）。</summary>
    public bool IsReacquiring => _reacquiring;

    /// <summary>連続して整合したサンプル数。</summary>
    public int StableSamples => _stableSamples;

    /// <summary>シークを発行した。着地が確認できるまで位置を使わない。</summary>
    public void InvalidateForPendingSeek()
    {
        _trusted = false;
        _reacquiring = false;
        ResetSamples();
    }

    /// <summary>着地を確認した（新しい位置のフレームが届いた）。その場で判定を再開する。</summary>
    public void MarkLanded()
    {
        _trusted = true;
        _reacquiring = false;
        ResetSamples();
    }

    /// <summary>時間切れで保留を解除した。位置が安定したことを確かめるまで判定を再開しない。</summary>
    public void RequireReacquire()
    {
        _trusted = false;
        _reacquiring = true;
        ResetSamples();
    }

    /// <summary>保留を破棄した・ロードした。位置を信頼して判定を再開する。</summary>
    public void Reset()
    {
        _trusted = true;
        _reacquiring = false;
        ResetSamples();
    }

    /// <summary>
    /// 再生位置のサンプルを観測する。時間切れ後の再確認中は、直前サンプルからの増分が
    /// 経過時間の ±20% 以内のサンプルが <see cref="RequiredStableSamples"/> 回続いたら信頼を戻す。
    /// </summary>
    public bool Observe(double positionSeconds, double nowSeconds)
    {
        if (_trusted)
            return true;
        if (!_reacquiring || !double.IsFinite(positionSeconds) || !double.IsFinite(nowSeconds))
            return false;

        bool stable = false;
        if (double.IsFinite(_lastPositionSeconds) && double.IsFinite(_lastAtSeconds))
        {
            double dt = nowSeconds - _lastAtSeconds;
            double moved = positionSeconds - _lastPositionSeconds;
            stable = dt > 0 && Math.Abs(moved - dt) <= dt * RateToleranceRatio;
        }

        _stableSamples = stable ? _stableSamples + 1 : 0;
        _lastPositionSeconds = positionSeconds;
        _lastAtSeconds = nowSeconds;

        if (_stableSamples >= _requiredSamples)
        {
            _trusted = true;
            _reacquiring = false;
        }

        return _trusted;
    }

    private void ResetSamples()
    {
        _stableSamples = 0;
        _lastPositionSeconds = double.NaN;
        _lastAtSeconds = double.NaN;
    }
}
