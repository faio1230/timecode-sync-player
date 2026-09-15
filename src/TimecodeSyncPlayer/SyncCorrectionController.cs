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
/// Jump はデッドバンドを超えたら補正シーク（連続 3 回で諦め）。
/// Smooth 失敗（shim 非対応・効かない）は状態として公開し、アプリが操作者に見せる。
/// </summary>
internal sealed class SyncCorrectionController
{
    public const double DeadbandSeconds = 0.020;
    public const double RateReturnBandSeconds = 0.010;
    public const double MaxRateDelta = 0.10;
    public const double TimeConstantSeconds = 1.0;
    public const int MaxConsecutiveJumpSeeks = 3;
    public static readonly TimeSpan IneffectiveWindow = TimeSpan.FromSeconds(2);
    public const double IneffectiveImprovementSeconds = 0.010;

    private bool _rateActive;
    private bool _smoothDisabled;
    private int _consecutiveJumpSeeks;
    private DateTime _windowStartedAt = DateTime.MinValue;
    private double _windowStartAbsResidual = double.NaN;

    /// <summary>shim が非フラッシュレート変更を受け付けない（1.18 未満・pipeline 拒否）。</summary>
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
            ? EvaluateJump(residualSeconds, targetSeconds)
            : EvaluateSmooth(residualSeconds, smoothAvailable, now);
    }

    /// <summary>トラック切替・モード切替・手動操作で状態を捨てる（次のトラックで再試行できる）。</summary>
    public void Reset()
    {
        _rateActive = false;
        _smoothDisabled = false;
        _consecutiveJumpSeeks = 0;
        ClearWindow();
    }

    private SyncCorrectionDecision EvaluateJump(double residualSeconds, double targetSeconds)
    {
        double abs = Math.Abs(residualSeconds);
        if (abs <= DeadbandSeconds)
        {
            _consecutiveJumpSeeks = 0;
            return SyncCorrectionDecision.Idle("jump-idle");
        }

        if (_consecutiveJumpSeeks >= MaxConsecutiveJumpSeeks)
            return SyncCorrectionDecision.Idle("jump-limit");

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
        else if (_windowStartAbsResidual - abs >= IneffectiveImprovementSeconds)
        {
            _windowStartedAt = now;
            _windowStartAbsResidual = abs;
        }
        else if (now - _windowStartedAt >= IneffectiveWindow)
        {
            _smoothDisabled = true;
            _rateActive = false;
            ClearWindow();
            return SyncCorrectionDecision.RateChange(1.0, "smooth-ineffective");
        }

        return SyncCorrectionDecision.RateChange(RateFor(residualSeconds), "smooth");
    }

    private static double RateFor(double residualSeconds)
    {
        double delta = Math.Clamp(residualSeconds / TimeConstantSeconds, -MaxRateDelta, MaxRateDelta);
        return 1.0 + delta;
    }

    private void ClearWindow()
    {
        _windowStartedAt = DateTime.MinValue;
        _windowStartAbsResidual = double.NaN;
    }
}
