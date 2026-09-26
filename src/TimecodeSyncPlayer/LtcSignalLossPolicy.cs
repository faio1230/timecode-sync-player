using Serilog;

namespace TimecodeSyncPlayer;

internal enum LtcSignalLossAction
{
    None,
    Pause,
    ResumeAndSync
}

/// <summary>
/// D27: 損失（Loss）と判定した理由。無音（信号断）か、解読は続いているが値が進まない
/// 保持（タイムコード停止）か。停止モードの表示文言と、停止位置を保持値へ合わせる
/// 1 回の着地の判断に使う。
/// </summary>
internal enum LtcSignalLossReason
{
    None,
    SignalLoss,
    TimecodeHeld
}

internal sealed record LtcSignalLossContext(
    LtcSignalLossMode Mode,
    bool SyncEnabled,
    bool IsMonitoring,
    bool IsGapActive,
    bool IsPlaybackPaused);

internal sealed class LtcSignalLossMonitoringState
{
    private bool _stoppedUnexpectedly;

    public bool IsDetectionActive(bool isReportedRunning) =>
        isReportedRunning || _stoppedUnexpectedly;

    public void MarkStarted() => _stoppedUnexpectedly = false;

    public bool MarkStopped(Exception? exception)
    {
        _stoppedUnexpectedly = exception != null;
        return !_stoppedUnexpectedly;
    }
}

/// <summary>
/// LTC signal-loss edge detection and recovery hysteresis without external side effects.
/// </summary>
internal sealed class LtcSignalLossPolicy
{
    private readonly TimeSpan _timeout;
    private readonly int _resumeFrameCount;
    private long? _lastValidFrameAtMilliseconds;
    private long? _lastHeldFrameAtMilliseconds;
    private LtcSignalLossReason _reason;
    private bool _isLost;
    private bool _pausedByPolicy;
    private bool _manualResumeSuppressesPause;
    private bool? _lastIsPlaybackPaused;
    private int _consecutiveResumeFrames;

    public LtcSignalLossPolicy(TimeSpan timeout, int resumeFrameCount)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (resumeFrameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(resumeFrameCount));

        _timeout = timeout;
        _resumeFrameCount = resumeFrameCount;
    }

    public bool ShouldSuppressSync => _isLost && _pausedByPolicy;
    public bool IsLost => _isLost;
    public bool IsPauseOwned => _pausedByPolicy;

    /// <summary>D27: 直近の損失判定の理由（保持か無音か）。</summary>
    public LtcSignalLossReason Reason => _reason;

    /// <summary>
    /// v0.5.2 段 1: できごとの入口。監視の開始・停止で初期化する（呼ぶ条件は LtcSyncController 側が
    /// 段 1 の前と同じに保つ）。
    /// </summary>
    public void OnLifecycle(SyncLifecycleEvent evt)
    {
        switch (evt)
        {
            case SyncLifecycleEvent.MonitoringStarted:
            case SyncLifecycleEvent.MonitoringStopped:
            case SyncLifecycleEvent.MonitorDeviceStopped:
                Reset();
                break;
        }
    }

    /// <summary>
    /// v0.5.3 段 3i: 信号断モードの変更を受ける（§6 の 9）。新しいモードがランスルーで、
    /// 信号断が止めているときだけ、ポリシー所有の一時停止を解く。損失の印（_isLost・理由）は
    /// 残す（ランスルーでは損失中でも ShouldSuppressSync は立たない）。戻り値は解いたか。
    /// </summary>
    public bool OnSignalLossModeChanged(LtcSignalLossMode newMode)
    {
        if (newMode != LtcSignalLossMode.RunThrough || !_pausedByPolicy)
            return false;
        _pausedByPolicy = false;
        _manualResumeSuppressesPause = false;
        return true;
    }

    public void Reset()
    {
        _lastValidFrameAtMilliseconds = null;
        _lastHeldFrameAtMilliseconds = null;
        _reason = LtcSignalLossReason.None;
        _isLost = false;
        _pausedByPolicy = false;
        _manualResumeSuppressesPause = false;
        _lastIsPlaybackPaused = null;
        _consecutiveResumeFrames = 0;
    }

    public LtcSignalLossAction ObserveValidFrame(long receivedAtMilliseconds, LtcSignalLossContext context)
    {
        if (!context.IsMonitoring)
        {
            Reset();
            return LtcSignalLossAction.None;
        }

        ObservePlaybackState(context);

        if (!_isLost)
        {
            _lastValidFrameAtMilliseconds = receivedAtMilliseconds;
            _lastHeldFrameAtMilliseconds = null;
            _reason = LtcSignalLossReason.None;
            _consecutiveResumeFrames = 0;
            return LtcSignalLossAction.None;
        }

        _lastValidFrameAtMilliseconds = receivedAtMilliseconds;
        _consecutiveResumeFrames++;
        if (_consecutiveResumeFrames < _resumeFrameCount)
            return LtcSignalLossAction.None;

        bool canApplyPolicyOwnedResume = SyncRules.CanResumeAfterSignalLoss(
            context.SyncEnabled, context.IsMonitoring, context.IsGapActive);
        if (_pausedByPolicy && !canApplyPolicyOwnedResume)
            return LtcSignalLossAction.None;

        _isLost = false;
        _reason = LtcSignalLossReason.None;
        _consecutiveResumeFrames = 0;
        _manualResumeSuppressesPause = false;
        bool shouldResume = _pausedByPolicy;
        _pausedByPolicy = false;
        SyncLifecycle.Record(SyncLifecycleEvent.SignalRecovered, "valid-frames");

        return shouldResume
            ? LtcSignalLossAction.ResumeAndSync
            : LtcSignalLossAction.None;
    }

    /// <summary>
    /// D27: 解読は続いているが値が進まない保持フレーム（Duplicate）の到着を記録する。
    /// 進行の時計（_lastValidFrameAtMilliseconds）は進めないので、保持が
    /// <see cref="_timeout"/> 続けば Evaluate が信号断と同じ損失として扱う。
    /// 損失の理由を「保持」に分けるためだけの観測で、判定の閾値は変えない。
    /// </summary>
    public void ObserveHeldFrame(long receivedAtMilliseconds, LtcSignalLossContext context)
    {
        if (!context.IsMonitoring)
        {
            Reset();
            return;
        }

        ObservePlaybackState(context);
        _lastHeldFrameAtMilliseconds = receivedAtMilliseconds;
    }

    /// <summary>
    /// D27-b: 保持（タイムコード停止）が理由の損失中に、値が動いた Jump フレームを観測する。
    /// 値の変化 1 枚で即復帰する（保持からの復帰の特別規則）。無音からの復帰は
    /// <see cref="ObserveValidFrame"/> の「有効フレーム N 枚」規則のまま変えない。
    /// 復帰後はこのフレーム時刻を進行の時計にし、続けて保持なら改めて損失になる。
    /// D27-c: 保持フレームの途切れで理由が信号断へ下がっていても、Jump が保持フレームの
    /// 直後（フレーム時刻で timeout 以内）なら保持からの復帰として扱う。処理遅延で
    /// Tick が保持フレームの時刻より後ろにずれても復帰を取りこぼさない。
    /// </summary>
    public LtcSignalLossAction ObserveJumpFrame(long receivedAtMilliseconds, LtcSignalLossContext context)
    {
        if (!context.IsMonitoring)
        {
            Reset();
            return LtcSignalLossAction.None;
        }

        ObservePlaybackState(context);

        if (!_isLost ||
            (_reason != LtcSignalLossReason.TimecodeHeld && !WasHeldRecently(receivedAtMilliseconds)))
            return LtcSignalLossAction.None;

        bool canApplyPolicyOwnedResume = SyncRules.CanResumeAfterSignalLoss(
            context.SyncEnabled, context.IsMonitoring, context.IsGapActive);
        if (_pausedByPolicy && !canApplyPolicyOwnedResume)
            return LtcSignalLossAction.None;

        _isLost = false;
        _reason = LtcSignalLossReason.None;
        _lastValidFrameAtMilliseconds = receivedAtMilliseconds;
        _lastHeldFrameAtMilliseconds = null;
        _consecutiveResumeFrames = 0;
        _manualResumeSuppressesPause = false;
        bool shouldResume = _pausedByPolicy;
        _pausedByPolicy = false;
        SyncLifecycle.Record(SyncLifecycleEvent.SignalRecovered, "held-jump");

        return shouldResume
            ? LtcSignalLossAction.ResumeAndSync
            : LtcSignalLossAction.None;
    }

    public LtcSignalLossAction Evaluate(long nowMilliseconds, LtcSignalLossContext context)
    {
        if (!context.IsMonitoring)
        {
            Reset();
            return LtcSignalLossAction.None;
        }

        ObservePlaybackState(context);

        if (_isLost)
        {
            if (_consecutiveResumeFrames > 0 &&
                _lastValidFrameAtMilliseconds.HasValue &&
                ElapsedMilliseconds(_lastValidFrameAtMilliseconds.Value, nowMilliseconds) >= _timeout.TotalMilliseconds)
            {
                _consecutiveResumeFrames = 0;
            }

            // D27: 保持フレームが途切れたら理由を信号断へ下げる（無音になった後の Jump を
            // 保持からの復帰として数えないため）。
            if (_reason == LtcSignalLossReason.TimecodeHeld && !WasHeldRecently(nowMilliseconds))
                _reason = LtcSignalLossReason.SignalLoss;

            return EvaluatePause(context);
        }

        if (!_lastValidFrameAtMilliseconds.HasValue ||
            ElapsedMilliseconds(_lastValidFrameAtMilliseconds.Value, nowMilliseconds) < _timeout.TotalMilliseconds)
            return LtcSignalLossAction.None;

        _isLost = true;
        _consecutiveResumeFrames = 0;
        _reason = WasHeldRecently(nowMilliseconds)
            ? LtcSignalLossReason.TimecodeHeld
            : LtcSignalLossReason.SignalLoss;
        // v0.5.4 段 0: 損失の確定を数える（ランスルーでは Pause ログが出ないため）。
        Log.Debug("sync.gate signal-loss-confirm elapsedMs={ElapsedMs:F1} reason={Reason}",
            ElapsedMilliseconds(_lastValidFrameAtMilliseconds.Value, nowMilliseconds), _reason);
        return EvaluatePause(context);
    }

    /// <summary>
    /// D27: 損失を確定した時点で直近に保持フレームが届いていれば「タイムコード停止」。
    /// 無音なら保持フレームは無い（または古い）ので「信号断」になる。
    /// </summary>
    private bool WasHeldRecently(long nowMilliseconds) =>
        _lastHeldFrameAtMilliseconds is long heldAt &&
        ElapsedMilliseconds(heldAt, nowMilliseconds) <= _timeout.TotalMilliseconds;

    private LtcSignalLossAction EvaluatePause(LtcSignalLossContext context)
    {
        if (!SyncRules.CanPauseForSignalLoss(
                context.Mode, context.SyncEnabled, context.IsGapActive,
                context.IsPlaybackPaused, _pausedByPolicy, _manualResumeSuppressesPause))
        {
            return LtcSignalLossAction.None;
        }

        _pausedByPolicy = true;
        return LtcSignalLossAction.Pause;
    }

    private void ObservePlaybackState(LtcSignalLossContext context)
    {
        if (_isLost &&
            !context.IsPlaybackPaused &&
            (_pausedByPolicy || _lastIsPlaybackPaused == true))
        {
            _pausedByPolicy = false;
            _manualResumeSuppressesPause = true;
        }

        _lastIsPlaybackPaused = context.IsPlaybackPaused;
    }

    private static long ElapsedMilliseconds(long earlier, long later) =>
        Math.Max(0, later - earlier);

    /// <summary>v0.5.2 段 0: ラッチが立っているかの読み取り専用の写し（特性テスト用。状態は変えない）。</summary>
    internal IReadOnlyDictionary<string, bool> LatchSnapshot() => new Dictionary<string, bool>
    {
        ["lost"] = _isLost,
        ["pausedByPolicy"] = _pausedByPolicy,
        ["manualResumeSuppressesPause"] = _manualResumeSuppressesPause,
    };
}
