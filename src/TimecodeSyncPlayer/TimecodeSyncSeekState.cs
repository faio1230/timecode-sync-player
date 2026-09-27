using Serilog;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

internal sealed class TimecodeSyncSeekState : ITimecodeSyncSeekState
{
    /// <summary>
    /// v0.5.4 U2: 直前の着地の記録（A の状態）。着地（門 6）が確定したときに 1 つだけ持ち、
    /// 着地直後の同じところへの再シークの抑止（門 9）はこの記録から導く（別の窓を持たない）。
    /// </summary>
    private readonly record struct SettledLanding(DateTime At, double TargetSeconds);

    private readonly TimeSpan _timeout;
    private DateTime _sentAt = DateTime.MinValue;
    private DateTime _settledAt = DateTime.MinValue;
    private SettledLanding? _lastSettled;
    private static readonly TimeSpan SettleCooldown = TimeSpan.FromMilliseconds(200);
    /// <summary>v0.5.4 U2: A の着地の記録が新しいと言える間（門 9 の抑止の長さ）。</summary>
    private static readonly TimeSpan SettledLandingSuppress = TimeSpan.FromMilliseconds(500);

    // ---- v0.5.4 U4: 位置の信頼（旧 PlaybackPositionTrust。着地の状態 A が持つ） ----

    /// <summary>位置の増分を許す割合。実時間 Δt に対し |Δpos − Δt| ≤ 0.20 × Δt（実測 ±20% のレート上限）。</summary>
    public const double RateToleranceRatio = 0.20;

    /// <summary>時間切れ解除後に、判定を再開するために必要な連続サンプル数。</summary>
    public const int RequiredStableSamples = 3;

    private bool _positionTrusted = true;
    private bool _reacquiring;
    private int _stableSamples;
    private double _lastObservedPositionSeconds = double.NaN;
    private double _lastObservedAtSeconds = double.NaN;
    private const double ContinuousPlaybackSettleSlackMultiplier = 2.0;
    // D20-b: 到達不能な pending を置き換える距離（tolerance の倍数）。
    private const double PendingSupersedeToleranceMultiplier = 4.0;
    // D37-b: 着地までの実測時間（秒）。素材ごとに学習し、シークと速度補正の分岐に使う。
    // 異常値（復帰不能なほど長い、0 に近すぎる）は学習に混ぜない。
    private const double LearnedSeekMinSeconds = 0.05;
    private const double LearnedSeekMaxSeconds = 10.0;
    private const double LearnedSeekEmaKeep = 0.7;
    private double _learnedSeekSeconds = double.NaN;

    // ---- v0.5.4 段 B1: 着地の状態（新しい判定。旧判定と並べて測る。B1 では判定に使わない） ----

    /// <summary>
    /// v0.5.4 段 B1: 安全の時間切れ（1 つ）。既存の 2 秒（旧 門 7 の保留）と 3 秒
    /// （ギャップの取り込みの <see cref="GapFreezeHandler.TimeoutSec"/>）の 2 つをまとめた値。
    /// shim のポンプの予算 4 秒（`native/gst-shim/src/tcs_gstreamer.cpp:243`）より短いので、
    /// ポンプが期限内に配信する場合でもアプリが先に「着地せず」へ移り得る。着地せずの後は
    /// 次のサンプルで追従中へ戻り、判定と次のシークを再開する（永久に止めない）。遅れて届いた
    /// フレームは次の判定で着地する（設計書 §9 の B1 の記録）。
    /// </summary>
    internal static readonly TimeSpan LandingSafetyTimeout = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _landingSafetyTimeout;
    private TimecodeSyncLandingPhase _landingPhase = TimecodeSyncLandingPhase.Following;
    private double _landingTargetSeconds;
    private ulong _landingSeekGeneration;
    private bool _landingGenerationLatched;
    private DateTime _landingSentAt = DateTime.MinValue;
    private bool _landingFirstGenerationSeen;
    private double _newLandingDelaySeconds = double.NaN;
    private double _oldLandingDelaySeconds = double.NaN;
    private bool _landingDelayCompareLogged;
    private int _landingFirstOutsideTotal;
    private int _landingMismatchTotal;
    private TimecodeSyncLandingRecord? _lastLanding;

    public TimecodeSyncSeekState()
        : this(TimeSpan.FromSeconds(2))
    {
    }

    public TimecodeSyncSeekState(TimeSpan timeout)
        : this(timeout, LandingSafetyTimeout)
    {
    }

    internal TimecodeSyncSeekState(TimeSpan timeout, TimeSpan landingSafetyTimeout)
    {
        _timeout = timeout;
        _landingSafetyTimeout = landingSafetyTimeout;
    }

    public bool HasPendingSeek { get; private set; }
    public double TargetSeconds { get; private set; }
    public TimecodeSyncSeekPendingStatus LastStatus { get; private set; } = TimecodeSyncSeekPendingStatus.None;

    /// <summary>
    /// v0.5.4 U4: A が着地を待っている間（保留中または時間切れ後の再確認中）。門 5・10・12 が
    /// 共有する 1 つの条件。位置を使った判定・補正と新しいシークはこの間は行わない。
    /// </summary>
    public bool IsWaitingForLanding => !_positionTrusted;

    /// <summary>v0.5.4 U4: 再生位置を粗い判定・補正に使えるか（門 10）。</summary>
    public bool IsPositionUsable => _positionTrusted;

    /// <summary>v0.5.4 U4: 時間切れ後の再確認中か（安定 3 サンプルを数えている間。門 11）。</summary>
    public bool IsReacquiring => _reacquiring;

    /// <summary>v0.5.4 U4: 連続して整合したサンプル数（テスト・記録用）。</summary>
    public int StableSamples => _stableSamples;

    /// <summary>D37-b: 着地までの実測時間（移動平均）。未学習は null。</summary>
    public double? LearnedSeekDurationSeconds =>
        double.IsFinite(_learnedSeekSeconds) ? _learnedSeekSeconds : null;

    /// <summary>D37-b: 素材が変わったとき（ロード）に学習を捨てる。</summary>
    public void ResetLearning() => _learnedSeekSeconds = double.NaN;

    // ---- v0.5.4 段 B1: 着地の状態（新しい判定。§9-2・§9-8） ----

    /// <summary>v0.5.4 段 B1: 着地の状態（追従中／着地待ち／着地せず）。</summary>
    public TimecodeSyncLandingPhase LandingPhase => _landingPhase;

    /// <summary>v0.5.4 段 B1: 新しい世代の最初のフレームが着地の窓の外だった回数（§9-8 の (c) 型）。</summary>
    public int LandingFirstFrameOutsideWindowCount => _landingFirstOutsideTotal;

    /// <summary>v0.5.4 段 B1: 新しい判定と古い判定の結論が食い違った回数。</summary>
    public int LandingMismatchCount => _landingMismatchTotal;

    /// <summary>v0.5.4 段 B1: 直近の新しい判定の着地の記録（着地していなければ null）。</summary>
    public TimecodeSyncLandingRecord? LastLanding => _lastLanding;

    /// <summary>v0.5.4 段 B1: 直近のシークを古い判定が着地した遅れ（秒。未着地は null）。</summary>
    public double? OldLandingDelaySeconds =>
        double.IsFinite(_oldLandingDelaySeconds) ? _oldLandingDelaySeconds : null;

    /// <summary>
    /// v0.5.4 段 B1: 着地の状態を初期化する（読み込み・手動移動・保留の外部破棄）。
    /// 旧判定の <see cref="Clear"/>／<see cref="ResetPositionTrust"/> の呼び出し元と同じ場所で呼ぶ。
    /// 累計のカウンタ（窓外・食い違い）は消さない（測定のため）。
    /// </summary>
    public void ResetLandingState()
    {
        _landingPhase = TimecodeSyncLandingPhase.Following;
        _landingGenerationLatched = false;
        _landingFirstGenerationSeen = false;
        _newLandingDelaySeconds = double.NaN;
        _oldLandingDelaySeconds = double.NaN;
        _landingDelayCompareLogged = false;
        _lastLanding = null;
    }

    /// <summary>
    /// v0.5.4 段 B1: 位置サンプルで着地の状態（新しい判定）を観測する。LTC のフレームの経路に
    /// 依らず、位置を照会するすべての場所（保持の Duplicate、UI タイマー、描画の tick）から呼ぶ。
    /// 着地の定義（§9-8）: 配信世代 >= シーク世代 かつ 配信フレームの位置（PTS）が
    /// target−tol〜target+2×tol の窓の中。
    /// </summary>
    public void ObserveLandingSample(in PlaybackPositionSample sample, double toleranceSeconds, DateTime now)
    {
        if (_landingPhase == TimecodeSyncLandingPhase.Following)
            return;
        if (_landingPhase == TimecodeSyncLandingPhase.FailedToLand)
        {
            // 着地せず（安全の時間切れ）の後は、次のサンプルで追従中へ戻る。
            _landingPhase = TimecodeSyncLandingPhase.Following;
            return;
        }

        if (!_landingGenerationLatched)
        {
            // シークの世代は、シークの後の最初の照会の現在世代から取る（ロードは内部で世代が
            // 2 回進むため、要求の前の世代とは比べない。§9-8）。
            _landingSeekGeneration = sample.CurrentGeneration;
            _landingGenerationLatched = true;
        }

        if (_landingSentAt != DateTime.MinValue && now - _landingSentAt >= _landingSafetyTimeout)
        {
            _landingPhase = TimecodeSyncLandingPhase.FailedToLand;
            Log.Debug(
                "sync.gate landing-safety-timeout elapsedMs={ElapsedMs:F1} target={Target:F3} gen={Generation}",
                (now - _landingSentAt).TotalMilliseconds, _landingTargetSeconds, _landingSeekGeneration);
            return;
        }

        if (sample.DeliveredGeneration < _landingSeekGeneration)
            return;   // シークの世代のフレームはまだ配信されていない

        bool withinWindow = IsWithinNewLandingWindow(sample.DeliveredSeconds, toleranceSeconds);
        if (!_landingFirstGenerationSeen)
        {
            _landingFirstGenerationSeen = true;
            if (!withinWindow)
            {
                // §9-8 の (c) 型の実測: 新しい世代の最初のフレームが着地の窓の外。0 でなければ
                // shim の通知（9-1 の後段）が要るかの材料になる。
                _landingFirstOutsideTotal++;
                Log.Debug(
                    "sync.gate landing-first-frame-outside-window delivered={Delivered:F3} target={Target:F3} tolerance={Tolerance:F4} deliveredGen={DeliveredGeneration} currentGen={CurrentGeneration}",
                    sample.DeliveredSeconds, _landingTargetSeconds, Math.Max(0, toleranceSeconds),
                    sample.DeliveredGeneration, sample.CurrentGeneration);
                return;
            }
        }

        if (!withinWindow)
            return;   // 外れた 2 枚目以降は数えない（着地の窓に入るまで待つ）

        // 着地。
        _landingPhase = TimecodeSyncLandingPhase.Following;
        _newLandingDelaySeconds = _landingSentAt == DateTime.MinValue
            ? 0.0
            : (now - _landingSentAt).TotalSeconds;
        _lastLanding = new TimecodeSyncLandingRecord(
            _landingTargetSeconds, _landingSeekGeneration, _newLandingDelaySeconds,
            sample.DeliveredSeconds, sample.DeliveredGeneration, sample.CurrentGeneration);
        Log.Debug(
            "sync.gate new-landing target={Target:F3} delayMs={DelayMs:F1} delivered={Delivered:F3} deliveredGen={DeliveredGeneration} currentGen={CurrentGeneration}",
            _landingTargetSeconds, _newLandingDelaySeconds * 1000.0, sample.DeliveredSeconds,
            sample.DeliveredGeneration, sample.CurrentGeneration);
        if (HasPendingSeek && !HasReachedSeekTarget(sample.Seconds, toleranceSeconds))
        {
            // 新しい判定は着地、古い判定（照会した位置の窓）はまだ、の食い違い。
            _landingMismatchTotal++;
            Log.Debug(
                "sync.gate landing-state-mismatch new=landed old=waiting query={Query:F3} delivered={Delivered:F3} target={Target:F3}",
                sample.Seconds, sample.DeliveredSeconds, _landingTargetSeconds);
        }
        LogLandingDelayCompareIfReady();
    }

    private bool IsWithinNewLandingWindow(double deliveredSeconds, double toleranceSeconds)
    {
        double boundedTolerance = Math.Max(0, toleranceSeconds);
        return deliveredSeconds >= _landingTargetSeconds - boundedTolerance &&
            deliveredSeconds <= _landingTargetSeconds + (boundedTolerance * ContinuousPlaybackSettleSlackMultiplier);
    }

    /// <summary>
    /// v0.5.4 段 B1: 新しい判定と古い判定の着地の遅れを、両方そろった時点で 1 回だけ対にして残す。
    /// </summary>
    private void LogLandingDelayCompareIfReady()
    {
        if (_landingDelayCompareLogged)
            return;
        if (!double.IsFinite(_newLandingDelaySeconds) || !double.IsFinite(_oldLandingDelaySeconds))
            return;
        _landingDelayCompareLogged = true;
        Log.Debug(
            "sync.gate landing-delay-compare newMs={NewMs:F1} oldMs={OldMs:F1} diffMs={DiffMs:F1} target={Target:F3}",
            _newLandingDelaySeconds * 1000.0, _oldLandingDelaySeconds * 1000.0,
            (_newLandingDelaySeconds - _oldLandingDelaySeconds) * 1000.0, _landingTargetSeconds);
    }

    public void BeginSeek(double targetSeconds, DateTime sentAt)
    {
        _settledAt = DateTime.MinValue;
        TargetSeconds = Math.Max(0, targetSeconds);
        _sentAt = sentAt;
        HasPendingSeek = true;
        LastStatus = TimecodeSyncSeekPendingStatus.Pending;
        // v0.5.4 U4: 着地が確認できるまで位置を使わない（門 10）。
        InvalidatePositionForPendingSeek();
        // v0.5.4 段 B1: 着地の状態（新）へシークと同期的に入る（§9-7 の 14）。
        _landingPhase = TimecodeSyncLandingPhase.WaitingForLanding;
        _landingTargetSeconds = TargetSeconds;
        _landingSentAt = sentAt;
        _landingGenerationLatched = false;
        _landingFirstGenerationSeen = false;
        _newLandingDelaySeconds = double.NaN;
        _oldLandingDelaySeconds = double.NaN;
        _landingDelayCompareLogged = false;
    }

    public void Clear()
    {
        _settledAt = DateTime.MinValue;
        HasPendingSeek = false;
        TargetSeconds = 0;
        _sentAt = DateTime.MinValue;
        LastStatus = TimecodeSyncSeekPendingStatus.None;
    }

    /// <summary>
    /// v0.5.3 段 3f: 直前の着地の記録だけを忘れる（読み込みで素材が変わるとき。§6 の 10）。
    /// <see cref="Clear"/> の意味は変えない（ほかの呼び出し元に影響させない）。
    /// </summary>
    public void ForgetLastSettled() => _lastSettled = null;

    /// <summary>
    /// v0.5.4 U4: 位置の信頼を初期化する（読み込み・手動移動・保留の外部破棄）。門 10 の状態。
    /// </summary>
    public void ResetPositionTrust()
    {
        _positionTrusted = true;
        _reacquiring = false;
        ResetObservedPosition();
    }

    /// <summary>
    /// v0.5.4 U4: 位置のサンプルを観測する（門 11）。時間切れ後の再確認中は、直前サンプルからの
    /// 増分が経過時間の ±20% 以内のサンプルが <see cref="RequiredStableSamples"/> 回続いたら
    /// 位置の信頼を戻す。位置が使える間は何も数えない。
    /// </summary>
    public bool ObservePlaybackPosition(double positionSeconds, double nowSeconds)
    {
        if (_positionTrusted)
            return true;
        if (!_reacquiring || !double.IsFinite(positionSeconds) || !double.IsFinite(nowSeconds))
            return false;

        bool stable = false;
        if (double.IsFinite(_lastObservedPositionSeconds) && double.IsFinite(_lastObservedAtSeconds))
        {
            double dt = nowSeconds - _lastObservedAtSeconds;
            double moved = positionSeconds - _lastObservedPositionSeconds;
            stable = dt > 0 && Math.Abs(moved - dt) <= dt * RateToleranceRatio;
        }

        _stableSamples = stable ? _stableSamples + 1 : 0;
        _lastObservedPositionSeconds = positionSeconds;
        _lastObservedAtSeconds = nowSeconds;

        if (_stableSamples >= RequiredStableSamples)
        {
            // v0.5.4 段 0: 時間切れ後の再確認（門 11）が終わったことを数える（遅延は TimedOut 行との対）。
            Log.Debug("sync.gate trust-reacquire samples={Samples} required={Required}",
                _stableSamples, RequiredStableSamples);
            _positionTrusted = true;
            _reacquiring = false;
        }

        return _positionTrusted;
    }

    /// <summary>v0.5.4 U4: 着地を待つ（位置を使わない）。保留の開始で入る。</summary>
    private void InvalidatePositionForPendingSeek()
    {
        _positionTrusted = false;
        _reacquiring = false;
        ResetObservedPosition();
    }

    /// <summary>v0.5.4 U4: 着地を確認した（その場で位置の判定を再開する）。</summary>
    private void MarkPositionLanded()
    {
        _positionTrusted = true;
        _reacquiring = false;
        ResetObservedPosition();
    }

    /// <summary>v0.5.4 U4: 着地できなかった（時間切れ・置き換え）。安定 3 サンプルまで再開しない。</summary>
    private void RequirePositionReacquire()
    {
        _positionTrusted = false;
        _reacquiring = true;
        ResetObservedPosition();
    }

    private void ResetObservedPosition()
    {
        _stableSamples = 0;
        _lastObservedPositionSeconds = double.NaN;
        _lastObservedAtSeconds = double.NaN;
    }

    public bool ShouldSuppressSeek(double playbackSeconds, double toleranceSeconds, DateTime now,
        double requestedTargetSeconds = double.NaN)
    {
        if (!HasPendingSeek)
        {
            if (_lastSettled is { } settled
                && now - settled.At < SettledLandingSuppress
                && IsWithinSettledTarget(playbackSeconds, toleranceSeconds))
            {
                // v0.5.4 段 0: 着地後の抑止（門 9）を着地（門 6）と分けて数える。
                // v0.5.4 U2: A の着地の記録（_lastSettled）から導く。
                Log.Debug("sync.gate post-settle-suppress elapsedMs={ElapsedMs:F1} target={Target:F3}",
                    (now - settled.At).TotalMilliseconds, settled.TargetSeconds);
                LastStatus = TimecodeSyncSeekPendingStatus.Settled;
                return true;
            }

            LastStatus = TimecodeSyncSeekPendingStatus.None;
            return false;
        }

        if (HasReachedSeekTarget(playbackSeconds, toleranceSeconds))
        {
            if (_settledAt == DateTime.MinValue)
                _settledAt = now;

            if (now - _settledAt < SettleCooldown)
            {
                LastStatus = TimecodeSyncSeekPendingStatus.Pending;
                return true;
            }

            // D37-b: 着地までの実測時間を学習する（目標に到達したと最初に観測した時刻まで）。
            if (_sentAt != DateTime.MinValue)
                LearnSeekDuration((_settledAt == DateTime.MinValue ? now : _settledAt) - _sentAt);
            // v0.5.4 段 B1: 古い判定の着地の遅れを残し、新しい判定との差を対にする。
            _oldLandingDelaySeconds = _sentAt == DateTime.MinValue
                ? 0.0
                : (now - _sentAt).TotalSeconds;
            if (_landingPhase == TimecodeSyncLandingPhase.WaitingForLanding)
            {
                // 古い判定は着地、新しい判定はまだ待っている、の食い違い。
                _landingMismatchTotal++;
                Log.Debug(
                    "sync.gate landing-state-mismatch old=settled new=waiting target={Target:F3} pendingTarget={PendingTarget:F3}",
                    _landingTargetSeconds, TargetSeconds);
            }
            LogLandingDelayCompareIfReady();
            // v0.5.4 段 0: 着地の確定（門 6）を数える（従来は pending "Settled" 行を門 9 と共有していた）。
            Log.Debug("sync.gate seek-settled target={Target:F3} elapsedMs={ElapsedMs:F1}",
                TargetSeconds, (now - _sentAt).TotalMilliseconds);
            _lastSettled = new SettledLanding(now, TargetSeconds);
            MarkPositionLanded();
            Clear();
            LastStatus = TimecodeSyncSeekPendingStatus.Settled;
            return true;               // セットルティックも抑止（1-tick 隙間を閉じる）
        }

        // D20-b (ii): 到達不能な pending（例: 終端静止中の target 0）は、新しい要求が
        // pending の目標から離れていればその要求で置き換え、今回のシークを抑止しない。
        if (IsNewRequestFarFromPending(requestedTargetSeconds, toleranceSeconds))
        {
            // v0.5.4 段 0: 到達不能 pending の置き換え（門 8 の re-pend）を数える。
            Log.Debug("sync.gate pending-replace pendingTarget={PendingTarget:F3} requestedTarget={RequestedTarget:F3}",
                TargetSeconds, requestedTargetSeconds);
            TargetSeconds = Math.Max(0, requestedTargetSeconds);
            _sentAt = now;
            _settledAt = DateTime.MinValue;
            LastStatus = TimecodeSyncSeekPendingStatus.Pending;
            return false;
        }

        if (now - _sentAt >= _timeout)
        {
            // v0.5.4 段 0: 保留のタイムアウト（門 7）の実測時間を残す。
            Log.Debug("sync.gate pending-timeout elapsedMs={ElapsedMs:F1} target={Target:F3}",
                (now - _sentAt).TotalMilliseconds, TargetSeconds);
            ClearPendingAs(TimecodeSyncSeekPendingStatus.TimedOut);
            return false;
        }

        LastStatus = TimecodeSyncSeekPendingStatus.Pending;
        return true;
    }

    /// <summary>
    /// D38 (b): 未信頼のフレームで、要求が pending の目標からも現在位置からも 4×tolerance を
    /// 超えて離れているとき、到達不能な pending を捨てる（置き換えず、再確認とゲートを
    /// 通してからシークさせる。現在位置の近くの要求は、pending の着地観測を残すため捨てない）。
    /// 捨てたときは LastStatus = Superseded（位置の再確認へ入る）。
    /// </summary>
    public bool DiscardIfUnreachable(
        double requestedTargetSeconds, double toleranceSeconds, double playbackSeconds)
    {
        if (!HasPendingSeek || !IsNewRequestFarFromPending(requestedTargetSeconds, toleranceSeconds))
            return false;
        // いま着地の窓に入っている pending は、捨てずに既存の着地判定（Settled）へ渡す。
        if (HasReachedSeekTarget(playbackSeconds, toleranceSeconds))
            return false;
        // 現在位置の近くの要求は、pending の着地観測を残すため捨てない。
        if (Math.Abs(requestedTargetSeconds - playbackSeconds) <=
            Math.Max(0, toleranceSeconds) * PendingSupersedeToleranceMultiplier)
            return false;
        ClearPendingAs(TimecodeSyncSeekPendingStatus.Superseded);
        return true;
    }

    /// <summary>
    /// v0.5.4 U3: A の「着地できなかった」枝。時間切れ（門 7）と置き換え（門 8）のどちらも
    /// ここで保留を捨てて結果だけを残し、位置の再確認（門 11。安定 3 サンプル）へ 1 か所でつなぐ。
    /// </summary>
    private void ClearPendingAs(TimecodeSyncSeekPendingStatus status)
    {
        Clear();
        LastStatus = status;
        if (status is TimecodeSyncSeekPendingStatus.TimedOut or TimecodeSyncSeekPendingStatus.Superseded)
            RequirePositionReacquire();
    }

    /// <summary>
    /// D20-b: 新しい要求が pending の目標から離れているか。連続して進む LTC の経路では
    /// pending と要求はほぼ一致するため置き換えは起きない。
    /// </summary>
    private bool IsNewRequestFarFromPending(double requestedTargetSeconds, double toleranceSeconds)
    {
        if (!double.IsFinite(requestedTargetSeconds))
            return false;

        double distance = Math.Abs(requestedTargetSeconds - TargetSeconds);
        return distance > Math.Max(0, toleranceSeconds) * PendingSupersedeToleranceMultiplier;
    }

    private void LearnSeekDuration(TimeSpan elapsed)
    {
        double seconds = elapsed.TotalSeconds;
        if (seconds < LearnedSeekMinSeconds || seconds > LearnedSeekMaxSeconds)
            return;
        _learnedSeekSeconds = double.IsFinite(_learnedSeekSeconds)
            ? _learnedSeekSeconds * LearnedSeekEmaKeep + seconds * (1.0 - LearnedSeekEmaKeep)
            : seconds;
    }

    private bool HasReachedSeekTarget(double playbackSeconds, double toleranceSeconds)
    {
        double boundedTolerance = Math.Max(0, toleranceSeconds);
        double lowerBound = TargetSeconds - boundedTolerance;
        double upperBound = TargetSeconds + (boundedTolerance * ContinuousPlaybackSettleSlackMultiplier);

        return playbackSeconds >= lowerBound && playbackSeconds <= upperBound;
    }

    private bool IsWithinSettledTarget(double playbackSeconds, double toleranceSeconds)
    {
        if (_lastSettled is not { } settled)
            return false;

        return Math.Abs(playbackSeconds - settled.TargetSeconds) <= Math.Max(0, toleranceSeconds);
    }

    /// <summary>
    /// v0.5.2 段 0: ラッチが立っているかの読み取り専用の写し（特性テスト用。状態は変えない）。
    /// lastSettledRecent は、直近の着地の記録が <paramref name="now"/> の時点でまだ着地後の抑止
    /// （ShouldSuppressSeek の着地の記録）に効く状態か。
    /// </summary>
    internal IReadOnlyDictionary<string, bool> LatchSnapshot(DateTime now) => new Dictionary<string, bool>
    {
        ["pendingSeek"] = HasPendingSeek,
        ["lastSettledRecent"] = _lastSettled is { } settled &&
            now - settled.At < SettledLandingSuppress,
    };
}

public enum TimecodeSyncSeekPendingStatus
{
    None,
    Pending,
    Settled,
    TimedOut,
    /// <summary>D38 (b): 未信頼の間に、離れた新しい要求で到達不能な pending を捨てた。</summary>
    Superseded
}

/// <summary>v0.5.4 段 B1: 着地の状態（新しい判定）。§9-2 の状態機械。</summary>
public enum TimecodeSyncLandingPhase
{
    /// <summary>追従中（着地・着地せずの後）。判定する。</summary>
    Following,
    /// <summary>着地待ち（世代 g・目標）。判定しない。位置を使わない。</summary>
    WaitingForLanding,
    /// <summary>着地せず（安全の時間切れ）。次のサンプルで追従中へ戻る。</summary>
    FailedToLand,
}

/// <summary>v0.5.4 段 B1: 新しい判定の着地の記録（計測・テスト用）。</summary>
public readonly record struct TimecodeSyncLandingRecord(
    double TargetSeconds,
    ulong SeekGeneration,
    double DelaySeconds,
    double DeliveredSeconds,
    ulong DeliveredGeneration,
    ulong CurrentGeneration);
