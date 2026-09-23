using Serilog;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer;

public sealed class TimecodeSyncService
{
    private readonly ISyncDecisionEngine _engine;
    private readonly ITimecodeSyncSeekState _seekState;
    private readonly TimeProvider _timeProvider;
    private readonly SeekLatencyCompensator _latencyCompensator;
    // v0.5.1 項目 4: Continue の切替で、読み込みの所要ぶん先から読み込む（学習はトラック単位）。
    private readonly TrackSwitchLoadLead _switchLoadLead;
    // D37-b: シーク中・着地未確認の位置を判定に使わないための状態。
    private readonly PlaybackPositionTrust _positionTrust = new();
    // 0.4.5-A フェーズ 1: 評価位置（基準・世代から求めた shadow）を trace に併記する。
    // 判断には使わない。
    private readonly PlaybackPositionFeedback _positionFeedback = new();
    // D37-f: シーク所要の見積もり（0.4.5-C3 のスキャンから）。学習値が無い間だけ使う。
    // ロード直後の 1 回目のシークには学習値が無く（ロードで ResetLearning するため）、
    // D37-e の先行補償が 0 になっていた。その 1 回目が問題の起点だったので、
    // 素材のキーフレーム分布から所要を見積もって埋める。
    private double _seekCostHintSeconds;

    // 0.4.5-A フェーズ 2: 評価位置を判断にも使う。既定 off（フェーズ 1 の挙動）。
    // 実機で同等以上を確認してから既定を on にする。評価位置が得られないとき
    // （旧 DLL、世代不一致）は on でも従来の抑制に落ちる。
    private readonly bool _positionFeedbackEnabled =
        string.Equals(Environment.GetEnvironmentVariable(PositionFeedbackEnvironmentVariable), "on",
            StringComparison.OrdinalIgnoreCase);

    internal const string PositionFeedbackEnvironmentVariable = "TCS_SYNC_POSITION_FEEDBACK";
    private TimecodeSyncSeekPendingStatus _lastSeekStatus = TimecodeSyncSeekPendingStatus.None;
    private double _publishedSeekCostSeconds = double.NaN;
    // D37-b2/D37-d: ギャップ明け・トラック切替・追従開始の着地エピソード。速度補正優先を
    // やめてシークで着地させる。窓の中は 1 回目をシークで試し、**着地後に不足が実際に
    // 減ったか**を観測する（前進ガード）。減っていなければ窓を閉じて通常の判断に戻す。
    // 上限（5 秒 / 連続 3 シーク）はシークが縮まらない素材への最後の歯止め。
    private bool _seekLandingActive;
    private DateTime _seekLandingOpenedAt = DateTime.MinValue;
    private int _seekLandingSeeks;
    // D37-e: この着地エピソードの発生元（追従開始だけ先行量を有効にする）。
    private LandingOrigin _landingOrigin = LandingOrigin.Other;
    // D37-d 前進ガード: 直前に窓の中で発行したシークの不足と、着地観測待ち。
    private bool _landingAwaitingObservation;
    private double _landingSeekPreDeficitSeconds = double.NaN;
    private const double LandingProgressEpsilonSeconds = 0.001;
    private static readonly TimeSpan SeekLandingMaxWindow = TimeSpan.FromSeconds(5.0);
    private const int SeekLandingMaxSeeks = 3;

    private DateTime _lastSyncSeekAt = DateTime.MinValue;
    private volatile bool _isLoadingFile;
    // D27-b: ロード解除を、解除を起こした呼び出しと別の呼び出し（保持 LTC の再適用）でも
    // ちょうど 1 回だけ回収できるようにする。解除が起きたら立て、回収したら下ろす。
    private bool _fileLoadReleasePending;
    private DateTime _fileLoadStartedAt = DateTime.MinValue;
    private DateTime _fileLoadReleasedAt = DateTime.MinValue;
    private double _fileLoadStartPositionSeconds;
    private long _fileLoadStartedRenderedFrames;
    private SyncActionType _lastLoggedSyncAction = SyncActionType.None;
    private bool _lastLoggedDefaultVideoFps;
    private bool _lastLoggedDefaultTimecodeFps;

    private const double SeekDebounceMs = 250.0;
    private const double FileLoadPlaybackProgressSeconds = 0.08;
    private const long FileLoadRenderedFrameProgress = 2;
    private static readonly TimeSpan FileLoadTimeout = TimeSpan.FromSeconds(5);
    // D35: 描画フレーム・再生位置の進みを待ち続けない上限。ロード開始からこの時間が過ぎたら
    // 進捗条件を満たさなくても解除する（停止中のロードで解除が数秒残るのを防ぐ）。実素材は
    // プロファイル試行で 2.2〜2.5 秒かかるため 5 秒（既存の安全タイムアウトと同じ）。
    private static readonly TimeSpan FileLoadReleaseForceAfter = TimeSpan.FromSeconds(5);
    // D35: 解除を回収できる鮮度。ロード直後の 1 回だけを対象にし、数秒前の値を保持開始時に
    // 再適用して同期を壊さない（古い解除は破棄する）。
    private static readonly TimeSpan FileLoadReleasePendingMaxAge = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// T9: 粗い同期シークを発行した時点の通知（<see cref="ReportSeekSent"/> と同じ）。
    /// LtcSyncController が着地直後の Smooth 速度上限の窓を開く。Jump の補正シークも
    /// このメソッドを通るが、窓は Smooth だけが参照する。
    /// </summary>
    internal event Action? SeekIssued;

    public TimecodeSyncService(
        ISyncDecisionEngine engine,
        ITimecodeSyncSeekState seekState,
        TimeProvider? timeProvider = null)
        : this(engine, seekState, timeProvider, null)
    {
    }

    internal TimecodeSyncService(
        ISyncDecisionEngine engine,
        ITimecodeSyncSeekState seekState,
        TimeProvider? timeProvider,
        SeekLatencyCompensator? latencyCompensator,
        TrackSwitchLoadLead? switchLoadLead = null)
    {
        _engine = engine;
        _seekState = seekState;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _latencyCompensator = latencyCompensator ?? new SeekLatencyCompensator();
        _switchLoadLead = switchLoadLead ?? new TrackSwitchLoadLead();
    }

    public SyncDecision EvaluateDecision(double ltcSeconds, SyncPlaybackState state,
        PlaybackPositionSample? positionSample = null)
    {
        PublishSeekCost();

        // 0.4.5-A フェーズ 1: 評価位置は trace に併記するだけ（判断は現行のまま）。
        // 0.4.5-A フェーズ 2（TCS_SYNC_POSITION_FEEDBACK=on）: 評価位置を判断にも使う。
        bool hasEvalPosition = false;
        if (positionSample is { } sample)
        {
            state = WithShadow(state, ltcSeconds, sample);
            hasEvalPosition = state.EvalPositionSeconds.HasValue;
        }

        // D37-b: シーク中・着地未確認の間は位置を使った判定をしない。
        // フェーズ 2: 評価位置があれば、その区間も配信 PTS 基準で評価を続ける
        // （シーク中はクエリ値が目標で凍結し誤差 0 に見えるが、評価位置は実際に育つ）。
        // 評価位置が無いとき（旧 DLL・世代不一致で `_ex` が失敗）は従来どおり抑制する。
        if (!_positionTrust.IsTrusted && !(_positionFeedbackEnabled && hasEvalPosition))
        {
            if (_positionTrust.IsReacquiring)
                _positionTrust.Observe(state.PlaybackSeconds, NowSeconds());
            _engine.RecordShadow(ltcSeconds, state, "position-untrusted");
            return _engine.WhilePositionUntrusted(state);
        }

        // D37-d: 直前のシークの着地を観測できるフレームなら、不足が実際に減ったかを先に見る。
        // 減っていなければ窓を閉じ、このフレームから通常の判断（速度補正を含む）に戻す。
        ObserveLandingProgress(ltcSeconds, state);

        // D37-b2: 着地直後（ギャップ明け・トラック切替）は速度補正に任せず、シークで着地させる。
        // D37-d: その保護は前進が確認できる限り続ける（1 回の着地では収束しない素材のため）。
        // D37-e: 追従開始の窓だけは、シークの行き先に学習済みシーク所要を足す（上限なし。
        // 定常・ギャップ明け・切替では 0 のまま）。
        bool landingActive = IsSeekLandingWindowActive();
        double lookahead = landingActive && _landingOrigin == LandingOrigin.FollowStart
            ? _seekState.LearnedSeekDurationSeconds ?? _seekCostHintSeconds
            : 0.0;
        SyncPlaybackState effectiveState = landingActive
            ? state with
            {
                RateCatchUpAllowed = false,
                SeekTargetLookaheadSeconds = lookahead,
            }
            : state;
        SyncDecision decision = _engine.Decide(ltcSeconds, effectiveState);
        // D37-d: 到達（誤差が許容内）で着地エピソードを閉じる。上限は
        // IsSeekLandingWindowActive が閉じる（シーク連鎖への逆戻り防止）。
        if (decision.WithinTolerance)
            ObserveArrivalWhileLanding();
        // 前進ガード用: このシークの不足を覚えておく（ReportSeekSent で着地観測を arm する）。
        if (decision.Action == SyncActionType.Seek && _seekLandingActive)
            _landingSeekPreDeficitSeconds = Math.Abs(ltcSeconds - state.PlaybackSeconds);
        // D37-f: 先行量がどう決まったかを、シークを出すときだけ残す。0 になる理由
        // （窓が閉じている / 発生元が追従開始でない / 学習値もヒントも無い）を切り分ける。
        if (decision.Action == SyncActionType.Seek)
        {
            Log.Information(
                "Seek lookahead: value={Lookahead:F3}s windowActive={Active} origin={Origin} learned={Learned} hint={Hint:F3}s",
                lookahead, landingActive, _landingOrigin,
                _seekState.LearnedSeekDurationSeconds?.ToString("F3") ?? "none",
                _seekCostHintSeconds);
        }
        LogDecisionIfNeeded(decision, ltcSeconds, effectiveState.PlaybackSeconds);
        return decision;
    }

    /// <summary>
    /// 0.4.5-A フェーズ 1: ネイティブシーク中など、通常の同期評価が走らないフレームでも
    /// 評価位置（shadow）だけを trace に残す。判断には使わない。
    /// </summary>
    public void RecordPositionShadow(double ltcSeconds, SyncPlaybackState state,
        PlaybackPositionSample? positionSample, string reason)
    {
        if (!OutputTrace.Current.IsEnabled) return;
        if (positionSample is { } sample)
            state = WithShadow(state, ltcSeconds, sample);
        _engine.RecordShadow(ltcSeconds, state, reason);
    }

    /// <summary>0.4.5-A フェーズ 1: shadow の補正モード（MainWindow が配線。未配線は Smooth）。</summary>
    public Func<SyncCorrectionMode>? CorrectionModeSource { get; set; }

    /// <summary>0.4.5-A フェーズ 1: shadow の着地窓（±0.20）判定（MainWindow が配線。未配線は false）。</summary>
    public Func<bool>? CorrectionLandingActiveSource { get; set; }

    private SyncPlaybackState WithShadow(SyncPlaybackState state, double ltcSeconds,
        in PlaybackPositionSample sample)
    {
        PlaybackPositionReading reading = _positionFeedback.Observe(sample, state.VideoFps);
        double residualSeconds = ltcSeconds - reading.EvaluationSeconds;
        SyncCorrectionMode mode = CorrectionModeSource?.Invoke() ?? SyncCorrectionMode.Smooth;
        (double previewRate, string previewReason) = mode == SyncCorrectionMode.Smooth
            ? SyncCorrectionController.PreviewSmoothRate(
                residualSeconds, CorrectionLandingActiveSource?.Invoke() ?? false)
            : (0.0, "not-smooth");
        return state with
        {
            EvalPositionSeconds = reading.EvaluationSeconds,
            EvalDeltaSeconds = residualSeconds,
            EvalBasis = reading.Basis switch
            {
                PlaybackPositionBasis.Delivered => "delivered",
                PlaybackPositionBasis.Pipeline => "pipeline",
                _ => "none",
            },
            EvalDeliveredGeneration = sample.DeliveredGeneration,
            EvalCurrentGeneration = sample.CurrentGeneration,
            ShadowRate = previewRate,
            ShadowRateReason = previewReason,
        };
    }

    /// <summary>D37-b: いま再生位置を粗い判定・補正に使えるか。</summary>
    public bool IsPlaybackPositionUsable => _positionTrust.IsTrusted;

    /// <summary>
    /// D37-b2/D37-d: ギャップ明け・トラック切替・追従開始の着地を通知する。ここから
    /// 誤差が許容内に入る（または上限に達する）まで、速度補正優先を止める。
    /// D37-e: 追従開始だけ origin=FollowStart を渡し、シーク目標の先行量を有効にする。
    /// </summary>
    public void NotifyLanding(LandingOrigin origin = LandingOrigin.Other)
        => OpenSeekLanding(_timeProvider.GetUtcNow().UtcDateTime, origin);

    private void OpenSeekLanding(DateTime now, LandingOrigin origin)
    {
        // D37-f: 追従開始の窓が生きている間は、Other で発生元を上書きしない。
        //
        // 追従開始とロード成立は同じフレームで起きうる。NotifyLanding(FollowStart) の直後に
        // ReleaseFileLoad が OpenSeekLanding(Other) を呼ぶと、発生元が Other に戻り、
        // D37-e の先行補償が効かなくなる（実測: windowActive=true・origin=Other・hint=1.990 で
        // lookahead=0。シーク先が LTC と同値になり、追従開始に 16 秒かかっていた）。
        //
        // 窓そのものは開き直してよい（シーク回数と観測待ちはリセットする）。守りたいのは
        // 「この着地は追従開始である」という事実だけ。
        bool keepFollowStart = _seekLandingActive
            && _landingOrigin == LandingOrigin.FollowStart
            && origin != LandingOrigin.FollowStart;
        _seekLandingActive = true;
        _seekLandingOpenedAt = now;
        _seekLandingSeeks = 0;
        _landingAwaitingObservation = false;
        _landingSeekPreDeficitSeconds = double.NaN;
        if (!keepFollowStart)
            _landingOrigin = origin;
    }

    /// <summary>
    /// D37-g: 追従開始のエピソードを終わらせる（着地窓そのものは残す）。
    ///
    /// Single の境界ホールド中は位置がクリップ端に固定されるため、誤差が許容内に入ることは
    /// 設計上ありえず、<see cref="ObserveArrivalWhileLanding"/> が呼ばれない。結果として
    /// 追従開始の窓が上限の 5 秒まで開いたままになり、その間に起きた<b>無関係な</b>シーク
    /// （範囲外 LTC が範囲内へ巻き戻ったときの復帰シーク）まで D37-e の先行量を足していた。
    ///
    /// 実測（E2E S-3、1280x720・キーフレーム間隔 1 秒のフィクスチャ）: LTC 10.022 への復帰で
    /// 行き先が 10.322 になり、判定許容 0.3 秒をちょうど超えて着地。さらに LTC が止まっている
    /// 場面のため以後の同期評価が走らず、そのまま前へ流れて 15.0 まで離れた。
    ///
    /// 先行量は「シークの間にタイムコードが進むぶん」の見積もりなので、追従開始という
    /// 文脈が終わったら外す。窓（シーク優先・速度補正の抑止）は残してよい。
    ///
    /// <b>代償</b>: 範囲外で同期を入れてから範囲内へ入る運用では、その「最初の実質的な追従」の
    /// シークが先行量を失う。キーフレーム間隔が長い素材では、その遷移だけシークが 1 回増える
    /// （着地窓は到達まで続くので収束はする）。それでも外すのは、S-3 の場面ではタイムコードが
    /// 止まっており、<b>行き過ぎたまま補正が走らない</b>ほうが害が大きいため。
    /// 本来は「タイムコードが進んでいるか」で先行量を決めるべきだが、その信号を同期側へ
    /// 渡す仕組みがまだない（0.4.6 以降の課題）。
    ///
    /// 0.4.6: <b>LTC の Jump を適用したときにも終わらせる</b>（<paramref name="reason"/> = "ltc jump"）。
    /// ホールド解除だけでは足りなかった。検証機の S-3 で、端へのシークの着地に 0.655 秒かかり、
    /// ホールドが成立しないまま LTC が範囲内へ戻った。復帰シークに学習値 0.655 が乗り、
    /// 10.060 に対して 10.714 へ着地した。LTC が不連続に動いた時点で「シークの間に LTC が
    /// 進むぶん」という前提が崩れるので、ホールドの成否に関係なくそこで外す。
    /// </summary>
    public void EndFollowStartLanding(string reason)
    {
        if (!_seekLandingActive || _landingOrigin != LandingOrigin.FollowStart)
            return;
        _landingOrigin = LandingOrigin.Other;
        Log.Information("Seek landing: follow-start episode ended ({Reason})", reason);
    }

    private void CloseSeekLanding()
    {
        _seekLandingActive = false;
        _seekLandingSeeks = 0;
        _landingAwaitingObservation = false;
        _landingSeekPreDeficitSeconds = double.NaN;
    }

    private bool IsSeekLandingWindowActive()
    {
        if (!_seekLandingActive)
            return false;
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        double ageMs = (now - _seekLandingOpenedAt).TotalMilliseconds;
        if (_seekLandingSeeks >= SeekLandingMaxSeeks)
        {
            Serilog.Log.Information(
                "Timecode sync: landing window closed at the seek cap seeks={Seeks} ageMs={AgeMs:F0}",
                _seekLandingSeeks, ageMs);
            CloseSeekLanding();
            return false;
        }
        if (now - _seekLandingOpenedAt >= SeekLandingMaxWindow)
        {
            Serilog.Log.Information(
                "Timecode sync: landing window closed at the age cap ageMs={AgeMs:F0} seeks={Seeks}",
                ageMs, _seekLandingSeeks);
            CloseSeekLanding();
            return false;
        }
        return true;
    }

    private void ObserveArrivalWhileLanding()
    {
        if (!_seekLandingActive)
            return;
        Serilog.Log.Information(
            "Timecode sync: landing window closed on arrival ageMs={AgeMs:F0} seeks={Seeks}",
            (_timeProvider.GetUtcNow().UtcDateTime - _seekLandingOpenedAt).TotalMilliseconds,
            _seekLandingSeeks);
        CloseSeekLanding();
    }

    /// <summary>
    /// D37-d 前進ガード: 窓の中で発行したシークの着地で、不足が実際に減ったかを見る。
    /// 減っていなければ（前進なし）窓を閉じ、以降のフレームは通常の判断に戻す。
    /// 事前予測ではなく観測なので、シークが効かない帯でも 1 回で止まる。
    /// </summary>
    private void ObserveLandingProgress(double ltcSeconds, SyncPlaybackState state)
    {
        if (!_landingAwaitingObservation)
            return;
        _landingAwaitingObservation = false;
        if (!_seekLandingActive || double.IsNaN(_landingSeekPreDeficitSeconds))
            return;
        double post = Math.Abs(ltcSeconds - state.PlaybackSeconds);
        if (post >= _landingSeekPreDeficitSeconds - LandingProgressEpsilonSeconds)
        {
            Serilog.Log.Information(
                "Timecode sync: landing window closed without progress preMs={PreMs:F0} postMs={PostMs:F0} ageMs={AgeMs:F0} seeks={Seeks}",
                _landingSeekPreDeficitSeconds * 1000.0, post * 1000.0,
                (_timeProvider.GetUtcNow().UtcDateTime - _seekLandingOpenedAt).TotalMilliseconds,
                _seekLandingSeeks);
            CloseSeekLanding();
        }
    }

    public bool IsLoadingFile => _isLoadingFile;

    /// <summary>D27-b: 回収待ちのロード解除があるか。</summary>
    public bool HasPendingFileLoadRelease => _fileLoadReleasePending;

    public bool ShouldSuppressSeek(double playbackSeconds, double toleranceSeconds,
        double requestedTargetSeconds = double.NaN)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        if (_isLoadingFile)
        {
            if (now - _fileLoadStartedAt > FileLoadTimeout)
            {
                _isLoadingFile = false;    // 安全タイムアウト
                _lastSyncSeekAt = now;    // タイムアウト後もデバウンスを保護
            }
            else
                return true;               // ロード中は全シーク抑止
        }

        bool suppress = _seekState.ShouldSuppressSeek(playbackSeconds, toleranceSeconds, now,
            requestedTargetSeconds);

        if (_seekState.LastStatus is TimecodeSyncSeekPendingStatus.Settled or TimecodeSyncSeekPendingStatus.TimedOut)
        {
            Serilog.Log.Information(
                "Timecode sync pending {Status} playback={Playback:F3} tolerance={Tolerance:F4}",
                _seekState.LastStatus, playbackSeconds, toleranceSeconds);
        }

        // D37-b: 保留の決着を位置の信頼状態へ反映する（着地 = その場で再開、
        // 時間切れ = 位置が安定するまで判定を止める）。
        TrackSeekStatusTransition();
        return suppress;
    }

    public bool IsDebounced()
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        return (now - _lastSyncSeekAt).TotalMilliseconds < SeekDebounceMs;
    }

    public void ReportSeekSent(double targetSeconds)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        _lastSyncSeekAt = now;
        _latencyCompensator.MarkSeekSent();
        _seekState.BeginSeek(targetSeconds, now);
        // D37-a: シーク後は位置が飛ぶため、粗い判定のゲート履歴を切る。
        _engine.ResetSeekGate();
        // D37-b: 着地が確認できるまで、位置を使った判定をしない。
        _positionTrust.InvalidateForPendingSeek();
        _lastSeekStatus = TimecodeSyncSeekPendingStatus.Pending;
        // D37-d: 着地エピソード中のシークを数える（上限で窓を閉じる）。前進ガードの着地
        // 観測は、このシークが窓の中で出たときだけ arm する（窓の外のシークは対象外）。
        if (IsSeekLandingWindowActive())
        {
            _seekLandingSeeks++;
            _landingAwaitingObservation = !double.IsNaN(_landingSeekPreDeficitSeconds);
        }
        SeekIssued?.Invoke();
    }

    /// <summary>
    /// LoadFile 発行時に呼ぶ。シーク状態をクリアしロード中フラグを立てる。
    /// リファクタリング前の LoadFile 時 Clear() 動作を復元する。
    /// あわせて先行補償の着地測定を arm する（issuedQpc は LoadFile 発行直前の QPC、0 は現在時刻）。
    /// </summary>
    public void BeginFileLoad(double startPositionSeconds, long renderedFrameCount)
        => BeginFileLoad(startPositionSeconds, renderedFrameCount, loadIssuedQpc: 0);

    /// <summary>
    /// LoadFile 発行時に呼ぶ。loadIssuedQpc は LoadFile を発行した QPC（計測開始点）。
    /// </summary>
    internal void BeginFileLoad(double startPositionSeconds, long renderedFrameCount, long loadIssuedQpc)
    {
        _latencyCompensator.MarkLoadSent(loadIssuedQpc);
        // 切替の読み込みは、呼び出し側（Continue）がこの後で測定を始める。それ以外の読み込みは測らない。
        _switchLoadLead.CancelMeasurement();
        _isLoadingFile = true;
        _fileLoadReleasePending = false;
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        _fileLoadStartedAt = now;
        _fileLoadStartPositionSeconds = Math.Max(0, startPositionSeconds);
        _fileLoadStartedRenderedFrames = Math.Max(0, renderedFrameCount);
        _lastSyncSeekAt = now;                // デバウンスを更新（2.3 fix）
        _seekState.Clear();                    // 古い保留シーク状態をクリア（2.1 fix）
        // D37-a: ロードで位置が飛ぶため、粗い判定のゲート履歴を切る。
        _engine.ResetSeekGate();
        // D37-b: 素材が変わるので着地時間の学習を捨てる。保留はクリア済みなので位置は使える。
        _seekState.ResetLearning();
        _publishedSeekCostSeconds = double.NaN;
        _positionTrust.Reset();
        // 0.4.5-A: 素材が変わるので、配信 PTS の基準と実測レートを捨てる。
        _positionFeedback.Reset();
        _lastSeekStatus = TimecodeSyncSeekPendingStatus.None;
        // D37-b2: ロード（切替）も着地として扱い、直後の不足はシークで詰める。
        NotifyLanding();
    }

    /// <summary>
    /// HandleOnTrackSync で再生位置と描画フレームが進んだらロード状態を解除する。
    /// </summary>
    public bool TryMarkFileLoaded(double playbackSeconds, long renderedFrameCount)
    {
        if (!_isLoadingFile) return true;
        if (!double.IsFinite(playbackSeconds) || playbackSeconds < 0)
            return false;
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        if (now - _fileLoadStartedAt > FileLoadTimeout)
            return ReleaseFileLoad(now, "timeout");

        double playbackProgress = playbackSeconds - _fileLoadStartPositionSeconds;
        long renderedFrameProgress = renderedFrameCount - _fileLoadStartedRenderedFrames;
        if (playbackProgress < FileLoadPlaybackProgressSeconds ||
            renderedFrameProgress < FileLoadRenderedFrameProgress)
        {
            // D35: 停止（保持）などで描画フレーム・再生位置が進まなくても、ロード開始から
            // 一定時間で必ず解除する。解除後は従来どおり保持 LTC の 1 回再適用に回収される。
            if (now - _fileLoadStartedAt >= FileLoadReleaseForceAfter)
                return ReleaseFileLoad(now, "forced");
            return false;
        }

        return ReleaseFileLoad(now, "progress");
    }

    private bool ReleaseFileLoad(DateTime now, string reason)
    {
        _isLoadingFile = false;
        _fileLoadReleasePending = true;
        _fileLoadReleasedAt = now;
        _lastSyncSeekAt = now;                // ロード後デバウンスを再スタート
        // D37-b2: ロード成立が実際の着地。D37-d: ここから新しい着地エピソードを開く
        // （ロード中に開始したエピソードとシーク回数を引き継がない）。
        OpenSeekLanding(now, LandingOrigin.Other);
        if (reason != "progress")
            Serilog.Log.Information("Timecode sync: file load released ({Reason})", reason);
        return true;
    }

    public void ClearSeekState()
    {
        _seekState.Clear();
        // D37-a: 保留の破棄・手動移動の後はゲートの系列を切る。
        _engine.ResetSeekGate();
        // D37-b: 保留を破棄したので位置は使える（着地の確認は要求しない）。
        _positionTrust.Reset();
        _lastSeekStatus = TimecodeSyncSeekPendingStatus.None;
    }

    /// <summary>
    /// D20-b: 保持 LTC（Duplicate）では通常の同期経路（ApplySync）が走らないため、
    /// ロード解除だけをここで観測できるようにする。解除された回だけ true を返す。
    /// D27-b: 解除が同期コーディネーター側の完了（TryMarkFileLoaded）で先に起きた場合も、
    /// 未回収の解除を 1 回だけ返す（保持 LTC の値の再適用を取りこぼさない）。
    /// </summary>
    public bool PollFileLoadRelease(double playbackSeconds, long renderedFrameCount)
    {
        if (_isLoadingFile && TryMarkFileLoaded(playbackSeconds, renderedFrameCount))
        {
            // この呼び出しが解除を回収する。未回収フラグは残さない。
            _fileLoadReleasePending = false;
            return true;
        }

        if (_fileLoadReleasePending)
        {
            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
            _fileLoadReleasePending = false;
            // D35: 解除直後の 1 回だけ回収する。鮮度を過ぎた解除（保持開始の数秒前に
            // 解除された古い値）は再適用せず破棄する。
            if (now - _fileLoadReleasedAt <= FileLoadReleasePendingMaxAge)
                return true;
            Serilog.Log.Information(
                "Timecode sync: dropping stale file load release ageMs={AgeMs:F0}",
                (now - _fileLoadReleasedAt).TotalMilliseconds);
            return false;
        }

        return false;
    }

    public ITimecodeSyncSeekState SeekState => _seekState;

    /// <summary>先行補償の学習状態（トラックの引き当てとフレーム Ready 通知に使う）。</summary>
    internal SeekLatencyCompensator LatencyCompensator => _latencyCompensator;

    internal TrackSwitchLoadLead SwitchLoadLead => _switchLoadLead;

    private double NowSeconds() => _timeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>D37-b: 学習したシーク所要（未学習は保守的に 1.0 秒）をエンジンへ公開する。</summary>
    /// <summary>
    /// D37-f: 読み込んだ素材のキーフレーム分布から、シーク 1 回の所要を見積もって渡す。
    /// 学習値が入るまでの間だけ使われる（実測が入ればそちらが勝つ）。
    /// 0 以下で解除。
    /// </summary>
    public void SetSeekCostHintSeconds(double seconds)
    {
        _seekCostHintSeconds = double.IsFinite(seconds) && seconds > 0 ? seconds : 0.0;
        PublishSeekCost();
    }

    private void PublishSeekCost()
    {
        const double DefaultSeekCostSeconds = 1.0;
        // 学習値 > スキャンの見積もり > 既定値。
        // 既知の欠点（0.4.6 で直す）: 学習値は TimecodeSyncSeekState.LearnSeekDuration が
        // 「位置が目標に初めて到達した時刻」で時計を止めるため、その後の再開までの停止
        // （実測でギャップの 0.12 倍、M3 で 0.79 秒）が入っておらず、常に短く出る。
        // スキャンの見積もりは停止込みで較正してある（SeekCostPerGapSecond = 0.42）。
        // それでも学習値を優先するのは、1 回目のシークにはまだ学習値が無く、
        // そこではヒントが使われる（D37-f の狙いはそこ）ため。2 回目以降の過小評価は
        // 従来からの挙動で、ここで一緒に変えると効果の帰属が分からなくなる。
        double cost = _seekState.LearnedSeekDurationSeconds
            ?? (_seekCostHintSeconds > 0 ? _seekCostHintSeconds : DefaultSeekCostSeconds);
        if (Math.Abs(cost - _publishedSeekCostSeconds) <= 1e-9)
            return;
        _engine.UpdateSeekCostSeconds(cost);
        _publishedSeekCostSeconds = cost;
    }

    /// <summary>D37-b: 保留の状態遷移を位置の信頼状態へ反映する。</summary>
    private void TrackSeekStatusTransition()
    {
        TimecodeSyncSeekPendingStatus status = _seekState.LastStatus;
        if (status == _lastSeekStatus)
            return;
        TimecodeSyncSeekPendingStatus previous = _lastSeekStatus;
        _lastSeekStatus = status;
        if (status == TimecodeSyncSeekPendingStatus.Settled)
            _positionTrust.MarkLanded();
        else if (status == TimecodeSyncSeekPendingStatus.TimedOut)
            _positionTrust.RequireReacquire();
        else if (previous == TimecodeSyncSeekPendingStatus.Pending &&
                 status == TimecodeSyncSeekPendingStatus.None)
            // D37-b: 保留が外から破棄された（境界ホールド解除など）。着地を要求せず位置を使い直す。
            _positionTrust.Reset();
    }

    private void LogDecisionIfNeeded(SyncDecision decision, double ltcSeconds, double playbackSeconds)
    {
        bool shouldLog =
            decision.Action != _lastLoggedSyncAction ||
            decision.UsedDefaultVideoFps != _lastLoggedDefaultVideoFps ||
            decision.UsedDefaultTimecodeFps != _lastLoggedDefaultTimecodeFps;

        if (!shouldLog)
            return;

        _lastLoggedSyncAction = decision.Action;
        _lastLoggedDefaultVideoFps = decision.UsedDefaultVideoFps;
        _lastLoggedDefaultTimecodeFps = decision.UsedDefaultTimecodeFps;

        Serilog.Log.Information(
            "Timecode sync decision action={Action} ltc={Ltc:F3} playback={Playback:F3} target={Target:F3} delta={Delta:F3} tolerance={Tolerance:F4} videoFps={VideoFps:F3} timecodeFps={TimecodeFps:F3} defaultVideoFps={DefaultVideoFps} defaultTimecodeFps={DefaultTimecodeFps}",
            decision.Action, ltcSeconds, playbackSeconds, decision.TargetSeconds,
            decision.DeltaSeconds, decision.ToleranceSeconds, decision.VideoFpsUsed,
            decision.TimecodeFpsUsed, decision.UsedDefaultVideoFps,
            decision.UsedDefaultTimecodeFps);
    }
}

/// <summary>
/// D37-e: 着地エピソードの発生元。追従開始だけがシーク目標の先行量（学習済みシーク所要）を
/// 使う。ギャップ明け・トラック切替・ロードは Other（先行なし）。
/// </summary>
public enum LandingOrigin
{
    Other,
    FollowStart,
}
