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
    private long _fileLoadEpoch;
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
    // D37-b2/D37-d: ギャップ明け・トラック切替・追従開始の着地窓（SeekLandingWindow）。
    private readonly SeekLandingWindow _landing;

    private DateTime _lastSyncSeekAt = DateTime.MinValue;
    // v0.5.2 段 2e: 読み込みの状態（ロード中／解除の回収待ち）。
    private readonly FileLoadState _fileLoad = new();
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

    /// <summary>
    /// v0.5.3 段 3e: このサービスで起きたできごとを外へ伝える（いまは <see cref="BeginFileLoad"/> の
    /// FileLoad だけ）。LtcSyncController が購読し、Jump と保持値の 1 回適用のラッチを下ろす（§6 の 5）。
    /// </summary>
    internal event Action<SyncLifecycleEvent>? LifecycleRaised;

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
        SeekLatencyCompensator? latencyCompensator)
    {
        _engine = engine;
        _seekState = seekState;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _latencyCompensator = latencyCompensator ?? new SeekLatencyCompensator();
        _landing = new SeekLandingWindow(_timeProvider);
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
            SyncDecision untrusted = _engine.WhilePositionUntrusted(ltcSeconds, state);
            // D38 (b): 未信頼でも、離れた新しい要求なら到達不能な pending を捨てる
            // （タイムアウトを待たず、再確認とゲートを通してからシークする）。
            if (untrusted.RequestedTargetSeconds is double requestedTarget)
                SupersedeUnreachablePending(requestedTarget, untrusted.ToleranceSeconds, state.PlaybackSeconds);
            return untrusted;
        }

        // D37-d: 直前のシークの着地を観測できるフレームなら、不足が実際に減ったかを先に見る。
        // 減っていなければ窓を閉じ、このフレームから通常の判断（速度補正を含む）に戻す。
        _landing.ObserveProgress(ltcSeconds, state.PlaybackSeconds);

        // D37-b2: 着地直後（ギャップ明け・トラック切替）は速度補正に任せず、シークで着地させる。
        // D37-d: その保護は前進が確認できる限り続ける（1 回の着地では収束しない素材のため）。
        // D37-e: 追従開始の窓だけは、シークの行き先に学習済みシーク所要を足す（上限なし。
        // 定常・ギャップ明け・切替では 0 のまま）。
        bool landingActive = _landing.IsActive();
        double lookahead = landingActive && _landing.Origin == LandingOrigin.FollowStart
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
        // _landing.IsActive が閉じる（シーク連鎖への逆戻り防止）。
        if (decision.WithinTolerance)
            _landing.ObserveArrival();
        // 前進ガード用: このシークの不足を覚えておく（ReportSeekSent で着地観測を arm する）。
        if (decision.Action == SyncActionType.Seek)
            _landing.SetSeekDeficit(Math.Abs(ltcSeconds - state.PlaybackSeconds));
        // D37-f: 先行量がどう決まったかを、シークを出すときだけ残す。0 になる理由
        // （窓が閉じている / 発生元が追従開始でない / 学習値もヒントも無い）を切り分ける。
        if (decision.Action == SyncActionType.Seek)
        {
            Log.Information(
                "Seek lookahead: value={Lookahead:F3}s windowActive={Active} origin={Origin} learned={Learned} hint={Hint:F3}s",
                lookahead, landingActive, _landing.Origin,
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
        => _landing.NotifyLanding(origin);

    /// <summary>
    /// D37-g: 追従開始のエピソードを終わらせる（着地窓そのものは残す）。
    /// 詳しい経緯は <see cref="SeekLandingWindow.EndFollowStartLanding"/> を参照。
    /// </summary>
    public void EndFollowStartLanding(string reason)
        => _landing.EndFollowStartLanding(reason);

    public bool IsLoadingFile => _fileLoad.IsLoadingFile;

    /// <summary>D27-b: 回収待ちのロード解除があるか。</summary>
    public bool HasPendingFileLoadRelease => _fileLoad.HasPendingRelease;

    public bool ShouldSuppressSeek(double playbackSeconds, double toleranceSeconds,
        double requestedTargetSeconds = double.NaN)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        if (_fileLoad.IsLoadingFile)
        {
            if (now - _fileLoad.StartedAt > FileLoadTimeout)
            {
                _fileLoad.MarkTimedOut(); // 安全タイムアウト
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

    /// <summary>
    /// D38 (a): 同期を適用しないフレーム（保持の Duplicate など）でも、保留中のシークの着地判定を
    /// 行う。判定は位置の信頼の回復（<see cref="TrackSeekStatusTransition"/>）にだけ効き、
    /// シークは出さない（戻り値も使わない）。
    /// </summary>
    public void ObservePendingSeekLanding(double playbackSeconds, double toleranceSeconds)
    {
        if (!_seekState.HasPendingSeek)
            return;
        _ = ShouldSuppressSeek(playbackSeconds, toleranceSeconds);
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
        _landing.OnSeekSent();
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
    internal void BeginFileLoad(
        double startPositionSeconds, long renderedFrameCount, long loadIssuedQpc, string source = "load")
    {
        SyncLifecycle.Record(SyncLifecycleEvent.FileLoad, source);
        _latencyCompensator.MarkLoadSent(loadIssuedQpc);
        _fileLoadEpoch++;
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        _fileLoad.Begin(now, Math.Max(0, startPositionSeconds), Math.Max(0, renderedFrameCount));
        _lastSyncSeekAt = now;                // デバウンスを更新（2.3 fix）
        OnLifecycle(SyncLifecycleEvent.FileLoad);
        // v0.5.3 段 3e: FileLoad はサービスの OnLifecycle の中で起きるため、外へも伝える（§6 の 5）。
        LifecycleRaised?.Invoke(SyncLifecycleEvent.FileLoad);
        // D37-b2: ロード（切替）も着地として扱い、直後の不足はシークで詰める。
        NotifyLanding();
    }

    /// <summary>
    /// v0.5.3 段 3g: ギャップの読み込み（Freeze の取り込みと読み直し）用の口（§6 の 2 の残り）。
    /// <see cref="BeginFileLoad"/> と違い、ロード中の印を立てず、着地窓を開かず、デバウンスも更新せず、
    /// 位置の信頼・ゲート・学習にも触らない（一時停止のまま解除が最大 5 秒遅れるのを避ける。
    /// 設計 docs/design/v0.5.3-gap-load-entry.md とその親の承認）。
    /// </summary>
    internal void BeginGapFreezeLoad(string source)
    {
        SyncLifecycle.Record(SyncLifecycleEvent.GapFreezeLoad, source);
        _fileLoadEpoch++;
        _fileLoad.ClearReleasePending();
        _seekState.Clear();
        _seekState.ForgetLastSettled();
    }

    /// <summary>
    /// v0.5.2 段 1: できごとでこのクラス（と持っているシークの保留状態）のラッチを消す入口。
    /// 段 0 の寿命の表の「現状」の列どおりに消す（段 1 の前に BeginFileLoad と ClearSeekState の
    /// 呼び出し元にあった処理を、順番を変えずに移したもの）。
    /// </summary>
    internal void OnLifecycle(SyncLifecycleEvent evt)
    {
        switch (evt)
        {
            case SyncLifecycleEvent.FileLoad:
                _fileLoad.ClearReleasePending();
                _seekState.Clear();                    // 古い保留シーク状態をクリア（2.1 fix）
                // v0.5.3 段 3f: 直前の着地の記録も忘れる（前のファイルの着地目標で
                // 0.5 秒抑止しない。§6 の 10）。
                _seekState.ForgetLastSettled();
                // D37-a: ロードで位置が飛ぶため、粗い判定のゲート履歴を切る。
                _engine.ResetSeekGate();
                // D37-b: 素材が変わるので着地時間の学習を捨てる。保留はクリア済みなので位置は使える。
                _seekState.ResetLearning();
                _publishedSeekCostSeconds = double.NaN;
                _positionTrust.Reset();
                // 0.4.5-A: 素材が変わるので、配信 PTS の基準と実測レートを捨てる。
                _positionFeedback.Reset();
                _lastSeekStatus = TimecodeSyncSeekPendingStatus.None;
                break;
            case SyncLifecycleEvent.SyncModeChanged:
            case SyncLifecycleEvent.TimelineSeek:
                ClearSeekState();
                break;
            case SyncLifecycleEvent.SyncDisabled:
                ClearSeekState();
                // v0.5.3 段 3d: 同期の無効化でロード中の印を取り消す（§6 の 3）。
                CancelFileLoad(evt);
                break;
            case SyncLifecycleEvent.PlaybackStopped:
                // v0.5.3 段 3d: 停止でロード中の印を取り消す（§6 の 3）。
                CancelFileLoad(evt);
                break;
        }
    }

    /// <summary>
    /// v0.5.3 段 3d: ロード中と解除の回収待ちを取り消す（§6 の 3）。解除（<see cref="ReleaseFileLoad"/>）
    /// ではないため、着地窓を開かずデバウンスも更新しない。ロード中だったときだけログを 1 行残す。
    /// </summary>
    private void CancelFileLoad(SyncLifecycleEvent evt)
    {
        bool wasLoading = _fileLoad.IsLoadingFile;
        _fileLoad.Cancel();
        if (wasLoading)
            Log.Information("Timecode sync: file load cancelled by {Event}", evt);
    }

    /// <summary>
    /// HandleOnTrackSync で再生位置と描画フレームが進んだらロード状態を解除する。
    /// </summary>
    public bool TryMarkFileLoaded(double playbackSeconds, long renderedFrameCount)
    {
        if (!_fileLoad.IsLoadingFile) return true;
        if (!double.IsFinite(playbackSeconds) || playbackSeconds < 0)
            return false;
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        if (now - _fileLoad.StartedAt > FileLoadTimeout)
            return ReleaseFileLoad(now, "timeout");

        double playbackProgress = playbackSeconds - _fileLoad.StartPositionSeconds;
        long renderedFrameProgress = renderedFrameCount - _fileLoad.StartedRenderedFrames;
        if (playbackProgress < FileLoadPlaybackProgressSeconds ||
            renderedFrameProgress < FileLoadRenderedFrameProgress)
        {
            // D35: 停止（保持）などで描画フレーム・再生位置が進まなくても、ロード開始から
            // 一定時間で必ず解除する。解除後は従来どおり保持 LTC の 1 回再適用に回収される。
            if (now - _fileLoad.StartedAt >= FileLoadReleaseForceAfter)
                return ReleaseFileLoad(now, "forced");
            return false;
        }

        return ReleaseFileLoad(now, "progress");
    }

    private bool ReleaseFileLoad(DateTime now, string reason)
    {
        _fileLoad.Release(now);
        _lastSyncSeekAt = now;                // ロード後デバウンスを再スタート
        // D37-b2: ロード成立が実際の着地。D37-d: ここから新しい着地エピソードを開く
        // （ロード中に開始したエピソードとシーク回数を引き継がない）。
        _landing.OpenAt(now, LandingOrigin.Other);
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
        if (_fileLoad.IsLoadingFile && TryMarkFileLoaded(playbackSeconds, renderedFrameCount))
        {
            // この呼び出しが解除を回収する。未回収フラグは残さない。
            _fileLoad.CollectRelease();
            return true;
        }

        if (_fileLoad.HasPendingRelease)
        {
            DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
            _fileLoad.CollectRelease();
            // D35: 解除直後の 1 回だけ回収する。鮮度を過ぎた解除（保持開始の数秒前に
            // 解除された古い値）は再適用せず破棄する。
            if (now - _fileLoad.ReleasedAt <= FileLoadReleasePendingMaxAge)
                return true;
            Serilog.Log.Information(
                "Timecode sync: dropping stale file load release ageMs={AgeMs:F0}",
                (now - _fileLoad.ReleasedAt).TotalMilliseconds);
            return false;
        }

        return false;
    }

    public ITimecodeSyncSeekState SeekState => _seekState;

    /// <summary>先行補償の学習状態（トラックの引き当てとフレーム Ready 通知に使う）。</summary>
    internal SeekLatencyCompensator LatencyCompensator => _latencyCompensator;

    /// <summary>
    /// v0.5.1: ファイルを読み込むたびに 1 つ進む番号。読み込みをまたいで持ち越してはいけない判断
    /// （Single の端へのシークを出したかどうか）を、読み込みごとに区切るために使う。
    /// </summary>
    internal long FileLoadEpoch => _fileLoadEpoch;

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
        else if (status is TimecodeSyncSeekPendingStatus.TimedOut or TimecodeSyncSeekPendingStatus.Superseded)
            _positionTrust.RequireReacquire();
        else if (previous == TimecodeSyncSeekPendingStatus.Pending &&
                 status == TimecodeSyncSeekPendingStatus.None)
            // D37-b: 保留が外から破棄された（境界ホールド解除など）。着地を要求せず位置を使い直す。
            _positionTrust.Reset();
    }

    /// <summary>
    /// D38 (b): 未信頼のフレームで、到達不能な pending（要求が目標からも現在位置からも離れている）
    /// を捨てる。捨てた後は位置の再確認（3 サンプル）とゲートを通ってからシークする。
    /// </summary>
    private void SupersedeUnreachablePending(
        double requestedTargetSeconds, double toleranceSeconds, double playbackSeconds)
    {
        if (!_seekState.DiscardIfUnreachable(requestedTargetSeconds, toleranceSeconds, playbackSeconds))
            return;
        Serilog.Log.Information(
            "Timecode sync pending \"Superseded\" playback={Playback:F3} tolerance={Tolerance:F4}",
            playbackSeconds, toleranceSeconds);
        TrackSeekStatusTransition();
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

    /// <summary>
    /// v0.5.2 段 0: ラッチが立っているかの読み取り専用の写し（特性テスト用。状態は変えない）。
    /// seekLandingActive は着地エピソードが開いているか（上限による閉鎖は次の評価で起きるため、
    /// ここでは判定しない）。
    /// </summary>
    internal IReadOnlyDictionary<string, bool> LatchSnapshot() => new Dictionary<string, bool>
    {
        ["loadingFile"] = _fileLoad.IsLoadingFile,
        ["fileLoadReleasePending"] = _fileLoad.HasPendingRelease,
        ["seekLandingActive"] = _landing.IsOpen,
        // 開いている着地エピソードが追従開始のものか（先行量が効く状態）。
        ["followStartLanding"] = _landing.IsOpen && _landing.Origin == LandingOrigin.FollowStart,
        // 位置を判定に使わない状態か（シークの着地待ち・取り直し待ち）。
        ["positionUntrusted"] = !_positionTrust.IsTrusted,
    };
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
