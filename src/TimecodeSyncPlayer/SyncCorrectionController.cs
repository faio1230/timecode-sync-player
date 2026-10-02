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
/// v0.5.4 B4b（chase モデルの規則 2）: 不感帯は 1 映像フレーム
/// （<see cref="FrameDurationSeconds"/>。呼び出し側が fps から決めて渡す）。戻りバンドは 2ms のまま。
/// Jump はしきい値（T8: 80ms）を超えたら補正シーク（B6-24: 連鎖は計数と警告のログだけ。
/// シークは止めない）。
/// Smooth 失敗（shim 非対応・効かない）は状態として公開し、アプリが操作者に見せる。
/// </summary>
internal sealed class SyncCorrectionController
{
    /// <summary>
    /// v0.5.4 B4b（chase モデルの規則 2）: Smooth が動き出す残差（不感帯）= 1 映像フレーム。
    /// 映像 fps が不明なら LTC の 1 フレーム、どちらも不明なら 25fps の 1 フレーム（0.04 秒。
    /// 既存の「1 フレーム」の既定と同じ）。配信したフレームの PTS はフレーム単位でしか動かない
    /// ため、5ms 固定のままでは 1 フレーム未満の丸めの差を小さな段で追い続ける。
    /// </summary>
    public static double FrameDurationSeconds(double videoFps, double timecodeFps)
    {
        double fps = videoFps > 0 ? videoFps : timecodeFps;
        return fps > 0 ? 1.0 / fps : 0.04;
    }

    /// <summary>
    /// T2 段 3: 補正中にレートを 1.0 へ戻す残差。不感帯より十分小さく、2ms のままにする。
    /// </summary>
    public const double RateReturnBandSeconds = 0.002;

    public const double MaxRateDelta = 0.10;
    public const double TimeConstantSeconds = 1.0;

    /// <summary>
    /// B6-24: Jump 補正の連鎖で警告を出す回数。振る舞いの上限ではなくなった（シークは止めない）。
    /// </summary>
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
    /// T8: Jump がシークするしきい値。LTC 25fps の 40ms フレームが音声コールバック
    /// （50ms ごと）で届くため、ずれていなくても残差に ±20〜40ms の揺れが乗る。
    /// 揺れの幅を越える最小の値として 80ms（LTC 2 フレーム分）にする。
    /// Smooth のデッドバンド（T2 段 3: 5ms）とは別の値・別の名前。
    /// </summary>
    public const double JumpSeekThresholdSeconds = 0.080;

    /// <summary>
    /// T8/B6-24: 残差がしきい値の内側にこれだけ留まったら連続シーク回数（警告用の計数）を
    /// 0 に戻す。一瞬内側に入っただけで戻すと、揺れがしきい値を跨ぐたびに警告が再武装される。
    /// 揺れ 1 周期（40〜50ms）より十分長い 1.0 秒を初期値にする。
    /// </summary>
    public static readonly TimeSpan JumpSettleTime = TimeSpan.FromSeconds(1.0);

    private bool _rateActive;
    private bool _smoothDisabled;
    private int _consecutiveJumpSeeks;
    private bool _jumpLimitReachedLogged;
    private DateTime _jumpInsideSince = DateTime.MinValue;
    private DateTime _windowStartedAt = DateTime.MinValue;
    private double _windowStartAbsResidual = double.NaN;

    /// <summary>
    /// GStreamer 側で INSTANT_RATE_CHANGE が効かない場合（1.18 未満、またはパイプラインの
    /// demuxer / clock-synchronizing element が非対応）。shim の戻り値からアプリが設定する。
    /// </summary>
    public bool SmoothUnavailable { get; private set; }

    /// <summary>レートを出しても残差が縮まないため Smooth を諦めた。</summary>
    public bool SmoothDisabled => _smoothDisabled;

    /// <summary>
    /// v0.6.4 段 3（設計書 3-3）: 「効いていない」の検出（smooth-ineffective）が発火した回数。起動からの累計で、
    /// <see cref="Reset"/> では消さない（Sync hold summary の smoothIneffective）。数えるだけで判定には使わない。
    /// </summary>
    public int IneffectiveDetections { get; private set; }

    /// <summary>
    /// 0.4.5-A フェーズ 1: 状態を変えずに「出したとしたら」の Smooth レートだけを計算する
    /// （shadow 記録用。<see cref="Evaluate"/> は呼ばないので _rateActive / _smoothDisabled に
    /// 触れない）。式は Smooth と同じ（戻りバンド・渡された不感帯・上限 ±0.10）。
    /// </summary>
    public static (double Rate, string Reason) PreviewSmoothRate(
        double residualSeconds, double deadbandSeconds)
    {
        if (!double.IsFinite(residualSeconds))
            return (1.0, "invalid");
        double abs = Math.Abs(residualSeconds);
        if (abs <= RateReturnBandSeconds)
            return (1.0, "smooth-idle");
        if (abs <= deadbandSeconds)
            return (1.0, "smooth-deadband");
        return (RateFor(residualSeconds, MaxRateDelta), "smooth");
    }

    public SyncCorrectionDecision Evaluate(
        double residualSeconds,
        double targetSeconds,
        SyncCorrectionMode mode,
        bool smoothAvailable,
        DateTime now,
        double deadbandSeconds)
    {
        if (!double.IsFinite(residualSeconds))
            return SyncCorrectionDecision.Idle("invalid");

        return mode == SyncCorrectionMode.Jump
            ? EvaluateJump(residualSeconds, targetSeconds, now)
            : EvaluateSmooth(residualSeconds, smoothAvailable, now, deadbandSeconds);
    }

    /// <summary>トラック切替・モード切替・手動操作で状態を捨てる（次のトラックで再試行できる）。</summary>
    public void Reset()
    {
        _rateActive = false;
        _smoothDisabled = false;
        _consecutiveJumpSeeks = 0;
        _jumpLimitReachedLogged = false;
        _jumpInsideSince = DateTime.MinValue;
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
            {
                _consecutiveJumpSeeks = 0;
                _jumpLimitReachedLogged = false;
            }
            return SyncCorrectionDecision.Idle("jump-idle");
        }

        _jumpInsideSince = DateTime.MinValue;
        // v0.5.4 B6-24（chase モデルの表 24）: 連鎖の歯止めは振る舞いの門をやめ、計数と警告の
        // ログへ格下げした。上限に達してもシークは止めない。警告は 1 エピソード 1 回
        // （1 秒以上しきい値の内側に留まって数え直すと、次の連鎖でまた出す）。
        _consecutiveJumpSeeks++;
        if (_consecutiveJumpSeeks >= MaxConsecutiveJumpSeeks && !_jumpLimitReachedLogged)
        {
            _jumpLimitReachedLogged = true;
            Log.Warning("Jump correction chain consecutiveSeeks={Count}", _consecutiveJumpSeeks);
        }
        return SyncCorrectionDecision.Seek(targetSeconds, "jump");
    }

    private SyncCorrectionDecision EvaluateSmooth(
        double residualSeconds, bool available, DateTime now, double deadbandSeconds)
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

        // ヒステリシス: 未補正ならデッドバンド（1 映像フレーム）内では動かない。補正中は
        // 戻りバンドまで比例制御を続ける。
        if (!_rateActive && abs <= deadbandSeconds)
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
            IneffectiveDetections++;
            return SyncCorrectionDecision.RateChange(1.0, "smooth-ineffective");
        }

        return SyncCorrectionDecision.RateChange(RateFor(residualSeconds, MaxRateDelta), "smooth");
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
