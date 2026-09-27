using Serilog;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

/// <summary>
/// v0.5.4 段 B: 着地の状態（A）。追従中／着地待ち（世代 g・目標）／着地せず の 1 つの状態機械で、
/// 門 5・6・7・8・9・10・11・12 を畳んだ。着地の定義は §9-8（配信世代 >= シーク世代 かつ
/// 配信フレームの位置（PTS）が target−tol〜target+2×tol）。観測は位置を照会するすべての場所
/// （LTC のフレーム、保持の Duplicate、UI タイマー、描画の tick）から呼ぶ。
/// </summary>
internal sealed class TimecodeSyncSeekState : ITimecodeSyncSeekState
{
    /// <summary>
    /// v0.5.4 段 B2: 安全の時間切れ（1 つ）。既存の 2 秒（旧 門 7 の保留）と 3 秒（ギャップの
    /// 取り込みの <see cref="GapFreezeHandler.TimeoutSec"/>）の 2 つをまとめた値。shim のポンプの
    /// 予算 4 秒（`native/gst-shim/src/tcs_gstreamer.cpp:243`）より短いので、ポンプが期限内に
    /// 配信する場合でもアプリが先に「着地せず」へ移り得る。着地せずの後は次のサンプルで追従中へ
    /// 戻り、判定と次のシークを再開する（永久に止めない）。遅れて届いたフレームは次の判定で
    /// 着地する（設計書 §9-9）。
    /// </summary>
    internal static readonly TimeSpan LandingSafetyTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// v0.5.4 段 B2 の計測: 着地からこの時間以内の同期シーク・速度補正を「着地直後」として数える
    /// （旧 門 9 の着地後 500ms の抑止が隠していた量。門ではなく物差し）。
    /// </summary>
    internal static readonly TimeSpan PostLandingMeasureWindow = TimeSpan.FromMilliseconds(500);

    // D20-b (ii): 遠い新要求は到達不能な着地待ちを捨てる距離（tolerance の倍数）。
    private const double PendingSupersedeToleranceMultiplier = 4.0;
    // 着地の窓の上側の余白（連続再生の遅れぶん）。
    private const double ContinuousPlaybackSettleSlackMultiplier = 2.0;
    // D37-b: 着地までの実測時間（秒）。素材ごとに学習し、シークと速度補正の分岐に使う。
    // 異常値（復帰不能なほど長い、0 に近すぎる）は学習に混ぜない。
    private const double LearnedSeekMinSeconds = 0.05;
    private const double LearnedSeekMaxSeconds = 10.0;
    private const double LearnedSeekEmaKeep = 0.7;

    private readonly TimeSpan _landingSafetyTimeout;
    private TimecodeSyncLandingPhase _landingPhase = TimecodeSyncLandingPhase.Following;
    private double _targetSeconds;
    private ulong _landingSeekGeneration;
    private bool _landingGenerationLatched;
    private DateTime _sentAt = DateTime.MinValue;
    private bool _landingFirstGenerationSeen;
    // v0.5.4 段 B: 遠い新要求で着地待ちの目標を置き換えた（シークはまだ発行されていない）。
    // この間も着地待ち（位置を使わない）を維持し、置き換えのシークだけを通す。
    private bool _replacementPending;
    private double _newLandingDelaySeconds = double.NaN;
    private DateTime _lastLandingAt = DateTime.MinValue;
    private int _landingFirstOutsideTotal;
    private TimecodeSyncLandingRecord? _lastLanding;
    private double _learnedSeekSeconds = double.NaN;

    public TimecodeSyncSeekState()
        : this(LandingSafetyTimeout)
    {
    }

    internal TimecodeSyncSeekState(TimeSpan landingSafetyTimeout)
    {
        _landingSafetyTimeout = landingSafetyTimeout;
    }

    public bool HasPendingSeek => _landingPhase == TimecodeSyncLandingPhase.WaitingForLanding;
    public double TargetSeconds => _targetSeconds;
    public TimecodeSyncSeekPendingStatus LastStatus { get; private set; } = TimecodeSyncSeekPendingStatus.None;

    /// <summary>v0.5.4 段 B: 着地を待っている間（門 5・10・12 が共有する 1 つの条件）。</summary>
    public bool IsWaitingForLanding => _landingPhase == TimecodeSyncLandingPhase.WaitingForLanding;

    /// <summary>v0.5.4 段 B: 再生位置を粗い判定・補正に使えるか（門 10）。</summary>
    public bool IsPositionUsable => _landingPhase != TimecodeSyncLandingPhase.WaitingForLanding;

    /// <summary>v0.5.4 段 B: 着地の状態（追従中／着地待ち／着地せず）。</summary>
    public TimecodeSyncLandingPhase LandingPhase => _landingPhase;

    /// <summary>v0.5.4 段 B: 新しい世代の最初のフレームが着地の窓の外だった回数（§9-8 の (c) 型）。</summary>
    public int LandingFirstFrameOutsideWindowCount => _landingFirstOutsideTotal;

    /// <summary>v0.5.4 段 B: 直近の着地の記録（着地していなければ null）。</summary>
    public TimecodeSyncLandingRecord? LastLanding => _lastLanding;

    /// <summary>D37-b: 着地までの実測時間（移動平均）。未学習は null。</summary>
    public double? LearnedSeekDurationSeconds =>
        double.IsFinite(_learnedSeekSeconds) ? _learnedSeekSeconds : null;

    /// <summary>D37-b: 素材が変わったとき（ロード）に学習を捨てる。</summary>
    public void ResetLearning() => _learnedSeekSeconds = double.NaN;

    public void BeginSeek(double targetSeconds, DateTime sentAt)
    {
        _targetSeconds = Math.Max(0, targetSeconds);
        _sentAt = sentAt;
        // シークを出すのと同期的に着地待ちに入る（§9-7 の 14）。
        _landingPhase = TimecodeSyncLandingPhase.WaitingForLanding;
        _landingGenerationLatched = false;
        _landingFirstGenerationSeen = false;
        _replacementPending = false;
        _newLandingDelaySeconds = double.NaN;
        LastStatus = TimecodeSyncSeekPendingStatus.Pending;
    }

    /// <summary>
    /// v0.5.4 段 B: 着地待ちを外から破棄する（読み込み・手動移動・保留の外部破棄）。
    /// 着地の記録（<see cref="LastLanding"/>）は <see cref="ResetLandingState"/> が忘れる。
    /// </summary>
    public void Clear()
    {
        _targetSeconds = 0;
        _sentAt = DateTime.MinValue;
        _landingPhase = TimecodeSyncLandingPhase.Following;
        _landingGenerationLatched = false;
        _landingFirstGenerationSeen = false;
        _replacementPending = false;
        _newLandingDelaySeconds = double.NaN;
        LastStatus = TimecodeSyncSeekPendingStatus.None;
    }

    /// <summary>v0.5.4 段 B: 着地の状態を初期化する（読み込み・手動移動・保留の外部破棄）。</summary>
    public void ResetLandingState()
    {
        Clear();
        _lastLanding = null;
        _lastLandingAt = DateTime.MinValue;
    }

    /// <summary>
    /// v0.5.4 段 B: 着地待ちの間の新しいシークを抑止する（門 5・10・12）。
    /// 遠い新要求（pending の目標から 4×tol 超）は前の着地待ちを捨てて、今回のシークを止めない
    /// （門 8。次に <see cref="BeginSeek"/> が来たら新しい着地待ちに入る）。
    /// </summary>
    public bool ShouldSuppressSeek(double playbackSeconds, double toleranceSeconds, DateTime now,
        double requestedTargetSeconds = double.NaN)
    {
        if (_landingPhase != TimecodeSyncLandingPhase.WaitingForLanding)
            return false;

        if (_replacementPending)
        {
            // 置き換えのシークは、着地待ちを維持したまま通す（BeginSeek で新しい待ちに入る）。
            Log.Debug(
                "sync.gate pending-replace pendingTarget={PendingTarget:F3} requestedTarget={RequestedTarget:F3}",
                _targetSeconds, requestedTargetSeconds);
            return false;
        }

        if (IsNewRequestFarFromPending(requestedTargetSeconds, toleranceSeconds))
        {
            Log.Debug(
                "sync.gate pending-replace pendingTarget={PendingTarget:F3} requestedTarget={RequestedTarget:F3}",
                _targetSeconds, requestedTargetSeconds);
            _targetSeconds = requestedTargetSeconds;
            _sentAt = now;
            _replacementPending = true;
            LastStatus = TimecodeSyncSeekPendingStatus.Superseded;
            return false;
        }

        Log.Debug("sync.gate pending-suppress playback={Playback:F3} target={Target:F3}",
            playbackSeconds, _targetSeconds);
        LastStatus = TimecodeSyncSeekPendingStatus.Pending;
        return true;
    }

    /// <summary>
    /// v0.5.4 段 B: 開始位置つきの読み込みの着地待ちに入る（目標は位置の窓を使わず、読み込みの
    /// 世代の最初のフレームの配信で着地する。§9-7-3・18 を畳む方向）。
    /// </summary>
    public void BeginLoadWait(DateTime now)
    {
        _targetSeconds = double.NaN;
        _sentAt = now;
        _landingPhase = TimecodeSyncLandingPhase.WaitingForLanding;
        _landingGenerationLatched = false;
        _landingFirstGenerationSeen = false;
        _replacementPending = false;
        _newLandingDelaySeconds = double.NaN;
        LastStatus = TimecodeSyncSeekPendingStatus.Pending;
    }

    /// <summary>v0.5.4 段 B: 遠い新要求で着地待ちの目標を置き換え、そのシークを待っているか。</summary>
    public bool HasPendingReplacement => _replacementPending;

    /// <summary>
    /// D38 (b) / 門 8 / §9-2: 未信頼のフレームで、要求が着地待ちの目標からも現在位置からも
    /// 4×tolerance を超えて離れているとき、着地待ちの目標を置き換える（前の待ちを捨てない。
    /// 位置は使わないまま、置き換えのシークをその場で出し、<see cref="BeginSeek"/> が新しい
    /// 着地待ちを入れる。判定と補正の隙間を作らない）。
    /// </summary>
    public bool ReplaceWaitTarget(
        double requestedTargetSeconds, double toleranceSeconds, double playbackSeconds, DateTime now)
    {
        if (_landingPhase != TimecodeSyncLandingPhase.WaitingForLanding)
            return false;
        if (!IsNewRequestFarFromPending(requestedTargetSeconds, toleranceSeconds))
            return false;
        // 現在位置の近くの要求は、着地の観測を残すため置き換えない。
        if (Math.Abs(requestedTargetSeconds - playbackSeconds) <=
            Math.Max(0, toleranceSeconds) * PendingSupersedeToleranceMultiplier)
            return false;
        Log.Debug(
            "sync.gate pending-replace pendingTarget={PendingTarget:F3} requestedTarget={RequestedTarget:F3}",
            _targetSeconds, requestedTargetSeconds);
        _targetSeconds = requestedTargetSeconds;
        _sentAt = now;
        _replacementPending = true;
        LastStatus = TimecodeSyncSeekPendingStatus.Superseded;
        return true;
    }

    /// <summary>
    /// v0.5.4 段 B: 位置サンプルで着地の状態を観測する。LTC のフレームの経路に依らず、位置を
    /// 照会するすべての場所（保持の Duplicate、UI タイマー、描画の tick）から呼ぶ。
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

        if (_sentAt != DateTime.MinValue && now - _sentAt >= _landingSafetyTimeout)
        {
            _landingPhase = TimecodeSyncLandingPhase.FailedToLand;
            LastStatus = TimecodeSyncSeekPendingStatus.TimedOut;
            Log.Debug(
                "sync.gate landing-safety-timeout elapsedMs={ElapsedMs:F1} target={Target:F3} gen={Generation}",
                (now - _sentAt).TotalMilliseconds, _targetSeconds, _landingSeekGeneration);
            // v0.5.4 段 0 の G7 の計測名（意味は安全の時間切れ。旧 2 秒 → 新しい 3 秒）。
            Log.Debug("sync.gate pending-timeout elapsedMs={ElapsedMs:F1} target={Target:F3}",
                (now - _sentAt).TotalMilliseconds, _targetSeconds);
            return;
        }

        if (sample.DeliveredGeneration < _landingSeekGeneration)
            return;   // シークの世代のフレームはまだ配信されていない

        // 目標が無い（開始位置つきの読み込み）ときは、読み込みの世代の最初のフレームの配信で着地する。
        bool hasTarget = double.IsFinite(_targetSeconds);
        bool withinWindow = !hasTarget ||
            IsWithinNewLandingWindow(sample.DeliveredSeconds, toleranceSeconds);
        if (hasTarget && !_landingFirstGenerationSeen)
        {
            _landingFirstGenerationSeen = true;
            if (!withinWindow)
            {
                // §9-8 の (c) 型の実測: 新しい世代の最初のフレームが着地の窓の外。0 でなければ
                // shim の通知（9-1 の後段）が要るかの材料になる。
                _landingFirstOutsideTotal++;
                Log.Debug(
                    "sync.gate landing-first-frame-outside-window delivered={Delivered:F3} target={Target:F3} tolerance={Tolerance:F4} deliveredGen={DeliveredGeneration} currentGen={CurrentGeneration}",
                    sample.DeliveredSeconds, _targetSeconds, Math.Max(0, toleranceSeconds),
                    sample.DeliveredGeneration, sample.CurrentGeneration);
                return;
            }
        }

        if (!withinWindow)
            return;   // 外れた 2 枚目以降は数えない（着地の窓に入るまで待つ）

        // 着地。
        _landingPhase = TimecodeSyncLandingPhase.Following;
        LastStatus = TimecodeSyncSeekPendingStatus.Settled;
        // D37-b: 着地までの実測時間を学習する。
        if (_sentAt != DateTime.MinValue)
            LearnSeekDuration(now - _sentAt);
        _newLandingDelaySeconds = _sentAt == DateTime.MinValue
            ? 0.0
            : (now - _sentAt).TotalSeconds;
        _lastLandingAt = now;
        _lastLanding = new TimecodeSyncLandingRecord(
            _targetSeconds, _landingSeekGeneration, _newLandingDelaySeconds,
            sample.DeliveredSeconds, sample.DeliveredGeneration, sample.CurrentGeneration);
        Log.Debug(
            "sync.gate new-landing target={Target:F3} delayMs={DelayMs:F1} delivered={Delivered:F3} deliveredGen={DeliveredGeneration} currentGen={CurrentGeneration}",
            _targetSeconds, _newLandingDelaySeconds * 1000.0, sample.DeliveredSeconds,
            sample.DeliveredGeneration, sample.CurrentGeneration);
        // v0.5.4 段 0 の G6 の計測名（意味は着地の確定。旧は 200ms の cooldown 込み）。
        // v0.5.4 段 B3: 読み込みの着地（目標なし）は 門 6 の計測に混ぜない（シークの着地だけを数える）。
        if (hasTarget)
        {
            Log.Debug("sync.gate seek-settled target={Target:F3} elapsedMs={ElapsedMs:F1}",
                _targetSeconds, _newLandingDelaySeconds * 1000.0);
        }
    }

    /// <summary>
    /// v0.5.4 段 B2 の計測: 着地から <see cref="PostLandingMeasureWindow"/> 以内に出た同期シークを
    /// 数える（旧 門 9 の着地後 500ms の抑止が隠していた量。目標は問わない）。
    /// </summary>
    public void NotePostLandingSeekIssued(double targetSeconds, DateTime now)
    {
        if (_lastLandingAt == DateTime.MinValue)
            return;
        // v0.5.4 段 B3: 読み込みの着地（目標なし）は「着地直後」に数えない（旧 門 9 と同じくシークの着地の後）。
        if (_lastLanding is not { } landing || !double.IsFinite(landing.TargetSeconds))
            return;
        double elapsedMs = (now - _lastLandingAt).TotalMilliseconds;
        if (elapsedMs < 0 || elapsedMs > PostLandingMeasureWindow.TotalMilliseconds)
            return;
        Log.Debug("sync.gate post-landing-seek elapsedMs={ElapsedMs:F1} target={Target:F3}",
            elapsedMs, targetSeconds);
    }

    /// <summary>
    /// v0.5.4 段 B2 の計測: 着地から <see cref="PostLandingMeasureWindow"/> 以内に出た速度補正
    /// （rate.instant）を数える（旧 門 9 が隠していた量）。
    /// </summary>
    public void NotePostLandingRateApplied(double rate, DateTime now)
    {
        if (_lastLandingAt == DateTime.MinValue)
            return;
        // v0.5.4 段 B3: 読み込みの着地（目標なし）は「着地直後」に数えない（旧 門 9 と同じくシークの着地の後）。
        if (_lastLanding is not { } landing || !double.IsFinite(landing.TargetSeconds))
            return;
        double elapsedMs = (now - _lastLandingAt).TotalMilliseconds;
        if (elapsedMs < 0 || elapsedMs > PostLandingMeasureWindow.TotalMilliseconds)
            return;
        Log.Debug("sync.gate post-landing-rate elapsedMs={ElapsedMs:F1} rate={Rate:F4}", elapsedMs, rate);
    }

    /// <summary>
    /// D20-b: 新しい要求が着地待ちの目標から離れているか。連続して進む LTC の経路では
    /// 要求と目標はほぼ一致するため捨てることは起きない。
    /// </summary>
    private bool IsNewRequestFarFromPending(double requestedTargetSeconds, double toleranceSeconds)
    {
        if (!double.IsFinite(requestedTargetSeconds))
            return false;

        double distance = Math.Abs(requestedTargetSeconds - _targetSeconds);
        return distance > Math.Max(0, toleranceSeconds) * PendingSupersedeToleranceMultiplier;
    }

    private bool IsWithinNewLandingWindow(double deliveredSeconds, double toleranceSeconds)
    {
        double boundedTolerance = Math.Max(0, toleranceSeconds);
        return deliveredSeconds >= _targetSeconds - boundedTolerance &&
            deliveredSeconds <= _targetSeconds + (boundedTolerance * ContinuousPlaybackSettleSlackMultiplier);
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

    /// <summary>
    /// v0.5.4 段 B2: ラッチの読み取り専用の写し（特性テスト用。状態は変えない）。
    /// pendingSeek は着地待ちの有無。
    /// </summary>
    internal IReadOnlyDictionary<string, bool> LatchSnapshot(DateTime now) => new Dictionary<string, bool>
    {
        ["pendingSeek"] = HasPendingSeek,
    };
}

public enum TimecodeSyncSeekPendingStatus
{
    None,
    Pending,
    Settled,
    TimedOut,
    /// <summary>門 8: 遠い新しい要求で到達不能な着地待ちを捨てた。</summary>
    Superseded
}

/// <summary>v0.5.4 段 B: 着地の状態。§9-2 の状態機械。</summary>
public enum TimecodeSyncLandingPhase
{
    /// <summary>追従中（着地・着地せずの後、ロード成立の後）。判定する。</summary>
    Following,
    /// <summary>着地待ち（世代 g・目標）。判定しない。位置を使わない。</summary>
    WaitingForLanding,
    /// <summary>着地せず（安全の時間切れ）。次のサンプルで追従中へ戻る。</summary>
    FailedToLand,
}

/// <summary>v0.5.4 段 B: 着地の記録（計測・テスト用）。</summary>
public readonly record struct TimecodeSyncLandingRecord(
    double TargetSeconds,
    ulong SeekGeneration,
    double DelaySeconds,
    double DeliveredSeconds,
    ulong DeliveredGeneration,
    ulong CurrentGeneration);
