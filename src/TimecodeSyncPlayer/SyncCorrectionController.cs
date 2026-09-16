using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>T5: 同期補正モード。Smooth = レート微調整（既定）、Jump = フラッシュシーク。</summary>
public enum SyncCorrectionMode
{
    Smooth,
    Jump
}

public enum SyncCorrectionActionType
{
    None,
    SetRate,
    Seek
}

public sealed record SyncCorrectionDecision(
    SyncCorrectionActionType Action,
    double Rate,
    double TargetSeconds,
    string Reason)
{
    public static SyncCorrectionDecision Idle(string reason) =>
        new(SyncCorrectionActionType.None, 1.0, 0.0, reason);

    public static SyncCorrectionDecision RateChange(double rate, string reason) =>
        new(SyncCorrectionActionType.SetRate, rate, 0.0, reason);

    public static SyncCorrectionDecision Seek(double targetSeconds, string reason) =>
        new(SyncCorrectionActionType.Seek, 1.0, targetSeconds, reason);
}

/// <summary>
/// T5: 粗いデッドゾーン（6 フレーム）の内側で残差 e = effectiveLtc - playback を見る補正。
/// Smooth は比例制御 rate = 1 + clamp(e / T, -0.10, +0.10)（T=1.0s）でシークを発行しない。
/// T9: 着地直後の 1.0 秒だけ上限を ±0.20 に上げ、1 秒以内の収束を狙う。
/// T2 段 3: 残差の揺れが 2ms になったため、デッドバンド 5ms・戻りバンド 2ms に狭める。
/// Jump はしきい値（T8: 80ms）を超えたら補正シーク（連続 3 回で諦め、残差が 1 秒留まったら再開）。
/// Smooth 失敗（shim 非対応・効かない）は状態として公開し、アプリが操作者に見せる。
/// </summary>
internal sealed class SyncCorrectionController
{
    /// <summary>
    /// T2 段 3: Smooth が動き出す残差。サンプル時計で揺れが 2ms になり、20ms では
    /// +10ms 前後の残差がデッドバンドの縁に張り付いて詰められないため 5ms にする。
    /// </summary>
    public const double DeadbandSeconds = 0.005;

    /// <summary>
    /// T2 段 3: 補正中にレートを 1.0 へ戻す残差。デッドバンドと同じ理由で 2ms にする。
    /// </summary>
    public const double RateReturnBandSeconds = 0.002;

    public const double MaxRateDelta = 0.10;
    public const double TimeConstantSeconds = 1.0;
    public const int MaxConsecutiveJumpSeeks = 3;
    public static readonly TimeSpan IneffectiveWindow = TimeSpan.FromSeconds(2);
    public const double IneffectiveImprovementSeconds = 0.010;

    /// <summary>
    /// T2 段 3: 「効かない」判定を行う最小の残差（窓の開始時の |残差|）。
    /// これ未満の残差は 10ms 改善しようがなく、判定すると Smooth を誤って止める。
    /// 窓は |残差| が開始値から <see cref="IneffectiveImprovementSeconds"/> 以上動いたとき
    /// （改善・悪化とも）に取り直すので、小さい残差から始まって大きく育った場合も、
    /// 育った値で判定できる。この判定は shim がレート変更を拒む場合の検出なので、
    /// 大きい残差でだけ意味がある。
    /// </summary>
    public const double IneffectiveMinimumResidualSeconds = 0.030;

    /// <summary>
    /// T9: 着地直後の速度上限。V3 の収束基準（1 秒以内に ±80ms）に対し、残差約 100ms を
    /// ±0.10 で詰めると約 0.7 秒かかり基準を超えるため、着地直後だけ上限を倍にする。
    /// 定常状態は <see cref="MaxRateDelta"/> のまま（音程への影響を普段は抑える）。
    /// </summary>
    public const double LandingMaxRateDelta = 0.20;

    /// <summary>
    /// T9: 上限を <see cref="LandingMaxRateDelta"/> に上げる長さ。着地（トラック切替のロード成立・
    /// 粗い同期シークの発行）からこの間だけ。収束基準の 1 秒と同じ長さにして、窓の間に
    /// 残差を詰め切れるようにする。
    /// </summary>
    public static readonly TimeSpan LandingWindow = TimeSpan.FromSeconds(1.0);

    /// <summary>
    /// T8: Jump がシークするしきい値。LTC 25fps の 40ms フレームが音声コールバック
    /// （50ms ごと）で届くため、ずれていなくても残差に ±20〜40ms の揺れが乗る。
    /// 揺れの幅を越える最小の値として 80ms（LTC 2 フレーム分）にする。
    /// Smooth のデッドバンド（T2 段 3: 5ms）とは別の値・別の名前。
    /// </summary>
    public const double JumpSeekThresholdSeconds = 0.080;

    /// <summary>
    /// T8: 残差がしきい値の内側にこれだけ留まったら連続シーク回数を 0 に戻す。
    /// 一瞬内側に入っただけで戻すと、揺れがしきい値を跨ぐたびに上限 3 回が無効化され、
    /// 250〜300ms ごとのシークが続く。揺れ 1 周期（40〜50ms）より十分長い 1.0 秒を初期値にする。
    /// </summary>
    public static readonly TimeSpan JumpSettleTime = TimeSpan.FromSeconds(1.0);

    private bool _rateActive;
    private bool _smoothDisabled;
    private int _consecutiveJumpSeeks;
    private bool _jumpLimitReachedLogged;
    private DateTime _jumpInsideSince = DateTime.MinValue;
    private DateTime _windowStartedAt = DateTime.MinValue;
    private double _windowStartAbsResidual = double.NaN;
    private DateTime _landingAt = DateTime.MinValue;
    private bool _landingLimitActive;

    /// <summary>
    /// GStreamer 側で INSTANT_RATE_CHANGE が効かない場合（1.18 未満、またはパイプラインの
    /// demuxer / clock-synchronizing element が非対応）。shim の戻り値からアプリが設定する。
    /// </summary>
    public bool SmoothUnavailable { get; private set; }

    /// <summary>レートを出しても残差が縮まないため Smooth を諦めた。</summary>
    public bool SmoothDisabled => _smoothDisabled;

    public SyncCorrectionDecision Evaluate(
        double residualSeconds,
        double targetSeconds,
        SyncCorrectionMode mode,
        bool smoothAvailable,
        DateTime now)
    {
        if (!double.IsFinite(residualSeconds))
            return SyncCorrectionDecision.Idle("invalid");

        return mode == SyncCorrectionMode.Jump
            ? EvaluateJump(residualSeconds, targetSeconds, now)
            : EvaluateSmooth(residualSeconds, smoothAvailable, now);
    }

    /// <summary>
    /// T9: 着地（トラック切替のロード成立、粗い同期シークの発行）を通知する。着地直後の
    /// <see cref="LandingWindow"/> だけ Smooth の速度上限を <see cref="LandingMaxRateDelta"/> に
    /// 上げる。窓の間に再通知されたら、そこから 1.0 秒に取り直す。Jump はこの窓を参照しない。
    /// </summary>
    public void NotifyLanding(DateTime now) => _landingAt = now;

    /// <summary>トラック切替・モード切替・手動操作で状態を捨てる（次のトラックで再試行できる）。</summary>
    public void Reset()
    {
        _rateActive = false;
        _smoothDisabled = false;
        _consecutiveJumpSeeks = 0;
        _jumpLimitReachedLogged = false;
        _jumpInsideSince = DateTime.MinValue;
        _landingAt = DateTime.MinValue;
        _landingLimitActive = false;
        ClearWindow();
    }

    private SyncCorrectionDecision EvaluateJump(double residualSeconds, double targetSeconds, DateTime now)
    {
        double abs = Math.Abs(residualSeconds);
        if (abs <= JumpSeekThresholdSeconds)
        {
            // T8: 一瞬内側に入っただけでは連続回数を戻さない。内側に留まり続けた時間で戻す。
            if (_jumpInsideSince == DateTime.MinValue)
                _jumpInsideSince = now;
            else if (now - _jumpInsideSince >= JumpSettleTime)
                _consecutiveJumpSeeks = 0;
            return SyncCorrectionDecision.Idle("jump-idle");
        }

        _jumpInsideSince = DateTime.MinValue;

        if (_consecutiveJumpSeeks >= MaxConsecutiveJumpSeeks)
        {
            if (!_jumpLimitReachedLogged)
            {
                // 測定用（T8）: 上限に達した遷移を数えられるように、1 エピソード 1 行だけ出す。
                _jumpLimitReachedLogged = true;
                Log.Information(
                    "Jump correction limit reached consecutiveSeeks={Count}", _consecutiveJumpSeeks);
            }
            return SyncCorrectionDecision.Idle("jump-limit");
        }

        _jumpLimitReachedLogged = false;
        _consecutiveJumpSeeks++;
        return SyncCorrectionDecision.Seek(targetSeconds, "jump");
    }

    private SyncCorrectionDecision EvaluateSmooth(double residualSeconds, bool available, DateTime now)
    {
        SmoothUnavailable = !available;
        if (!available)
        {
            _rateActive = false;
            return SyncCorrectionDecision.Idle("smooth-unavailable");
        }

        if (_smoothDisabled)
        {
            _rateActive = false;
            return SyncCorrectionDecision.Idle("smooth-disabled");
        }

        double abs = Math.Abs(residualSeconds);
        if (abs <= RateReturnBandSeconds)
        {
            bool wasActive = _rateActive;
            _rateActive = false;
            ClearWindow();
            return wasActive
                ? SyncCorrectionDecision.RateChange(1.0, "smooth-settled")
                : SyncCorrectionDecision.Idle("smooth-idle");
        }

        // ヒステリシス: 未補正ならデッドバンド内では動かない。補正中は戻りバンドまで比例制御を続ける。
        if (!_rateActive && abs <= DeadbandSeconds)
            return SyncCorrectionDecision.Idle("smooth-idle");

        if (!_rateActive)
        {
            _rateActive = true;
            _windowStartedAt = now;
            _windowStartAbsResidual = abs;
        }
        else if (_windowStartAbsResidual - abs >= IneffectiveImprovementSeconds ||
                 abs - _windowStartAbsResidual >= IneffectiveImprovementSeconds)
        {
            // 改善でも悪化でも、窓の開始値から 10ms 以上動いたら時間を測り直す。
            // 悪化を無視すると、小さい残差（例 8ms）で窓が始まった後に shim がレートを
            // 無視して残差が育っても、開始値が小さいままゲートが開かず検出できない。
            _windowStartedAt = now;
            _windowStartAbsResidual = abs;
        }
        else if (now - _windowStartedAt >= IneffectiveWindow &&
                 _windowStartAbsResidual >= IneffectiveMinimumResidualSeconds)
        {
            _smoothDisabled = true;
            _rateActive = false;
            ClearWindow();
            return SyncCorrectionDecision.RateChange(1.0, "smooth-ineffective");
        }

        bool landingActive = _landingAt != DateTime.MinValue && now - _landingAt < LandingWindow;
        if (landingActive != _landingLimitActive)
        {
            // 測定用（T9）: 上限が切り替わった回数を数えられるように、切り替わったときだけ出す。
            _landingLimitActive = landingActive;
            Log.Information(
                "Smooth rate limit switched to {Limit:F2} landingWindow={LandingWindow}",
                landingActive ? LandingMaxRateDelta : MaxRateDelta,
                landingActive ? "active" : "ended");
        }

        double maxDelta = landingActive ? LandingMaxRateDelta : MaxRateDelta;
        return SyncCorrectionDecision.RateChange(RateFor(residualSeconds, maxDelta), "smooth");
    }

    private static double RateFor(double residualSeconds, double maxRateDelta)
    {
        double delta = Math.Clamp(residualSeconds / TimeConstantSeconds, -maxRateDelta, maxRateDelta);
        return 1.0 + delta;
    }

    private void ClearWindow()
    {
        _windowStartedAt = DateTime.MinValue;
        _windowStartAbsResidual = double.NaN;
    }
}
