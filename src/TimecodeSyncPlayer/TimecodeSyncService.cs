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
    private double _publishedSeekCostSeconds = double.NaN;

    private DateTime _lastSyncSeekAt = DateTime.MinValue;
    // v0.5.2 段 2e: 読み込みの状態（ロード中／解除の回収待ち）。
    private readonly FileLoadState _fileLoad = new();
    // v0.5.4 段 B2: 着地待ちの決着（Settled/TimedOut/Superseded）を 1 回だけ Information に残す。
    private TimecodeSyncSeekPendingStatus _lastLoggedSeekStatus = TimecodeSyncSeekPendingStatus.None;
    private SyncActionType _lastLoggedSyncAction = SyncActionType.None;
    private bool _lastLoggedDefaultVideoFps;
    private bool _lastLoggedDefaultTimecodeFps;

    private const double SeekDebounceMs = 250.0;
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
        // v0.5.4 段 B: A（着地の状態）の 1 つの条件（着地を待っている間）で止める（門 10・12）。
        // フェーズ 2: 評価位置があれば、その区間も配信 PTS 基準で評価を続ける
        // （シーク中はクエリ値が目標で凍結し誤差 0 に見えるが、評価位置は実際に育つ）。
        // 評価位置が無いとき（旧 DLL・世代不一致で `_ex` が失敗）は従来どおり抑制する。
        if (_seekState.IsWaitingForLanding && !(_positionFeedbackEnabled && hasEvalPosition))
        {
            _engine.RecordShadow(ltcSeconds, state, "position-untrusted");
            SyncDecision untrusted = _engine.WhilePositionUntrusted(ltcSeconds, state);
            // v0.5.4 段 B / 門 8・§9-2: 未信頼でも、離れた新しい要求なら着地待ちの目標を置き換え、
            // その場ですぐシークを出す（位置は使わないまま、同じ手順で新しい着地待ちに入る）。
            // 13 のゲートと 15 の速度補正は通さない（古い位置で判定・補正が走る隙間を作らない）。
            if (_seekState.HasPendingReplacement)
                return ReplacementSeekDecision(_seekState.TargetSeconds, state, untrusted);
            if (untrusted.RequestedTargetSeconds is double requestedTarget &&
                _seekState.ReplaceWaitTarget(requestedTarget, untrusted.ToleranceSeconds,
                    state.PlaybackSeconds, _timeProvider.GetUtcNow().UtcDateTime))
            {
                Serilog.Log.Information(
                    "Timecode sync pending \"Superseded\" playback={Playback:F3} tolerance={Tolerance:F4}",
                    state.PlaybackSeconds, untrusted.ToleranceSeconds);
                return ReplacementSeekDecision(requestedTarget, state, untrusted);
            }
            return untrusted;
        }

        // v0.5.4 B6b-16/23: 着地窓（D37-b2/d/e/f）は畳んだ。relocate の直後の 1 サンプルは
        // varispeed しない（補正の入口で止める）。シークの可否は 15 の閾値（max(tol, 学習値)）
        // と 13 のゲートだけで決まる。
        SyncDecision decision = _engine.Decide(ltcSeconds, state);
        LogDecisionIfNeeded(decision, ltcSeconds, state.PlaybackSeconds);
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

    private SyncPlaybackState WithShadow(SyncPlaybackState state, double ltcSeconds,
        in PlaybackPositionSample sample)
    {
        PlaybackPositionReading reading = _positionFeedback.Observe(sample, state.VideoFps);
        double residualSeconds = ltcSeconds - reading.EvaluationSeconds;
        SyncCorrectionMode mode = CorrectionModeSource?.Invoke() ?? SyncCorrectionMode.Smooth;
        (double previewRate, string previewReason) = mode == SyncCorrectionMode.Smooth
            ? SyncCorrectionController.PreviewSmoothRate(
                residualSeconds,
                SyncCorrectionController.FrameDurationSeconds(state.VideoFps, state.TimecodeFps))
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

    /// <summary>D37-b: いま再生位置を粗い判定・補正に使えるか（v0.5.4 U4: A の状態から導く。門 10）。</summary>
    public bool IsPlaybackPositionUsable => _seekState.IsPositionUsable;

    /// <summary>
    /// v0.5.4 U4: A が着地を待っている間か（保留中または時間切れ後の再確認中）。門 5・10・12 の
    /// 1 つの条件。補正の評価（門 10 の rate.instant の抑止）もこれで止める。
    /// </summary>
    public bool IsWaitingForLanding => _seekState.IsWaitingForLanding;

    /// <summary>
    /// D37-b2/D37-d: ギャップ明け・トラック切替・追従開始の着地を通知する。ここから
    /// 誤差が許容内に入る（または上限に達する）まで、速度補正優先を止める。
    /// D37-e: 追従開始だけ origin=FollowStart を渡し、シーク目標の先行量を有効にする。
    /// </summary>
    public bool IsLoadingFile => _fileLoad.IsLoadingFile;

    /// <summary>
    /// v0.5.4 B6b-16/23: relocate（シーク）・読み込みの着地を観測した直後の 1 サンプルだけ
    /// true を返す（消費する）。呼び出し側（補正の入口）はこのサンプルで varispeed しない。
    /// </summary>
    public bool ConsumeFirstSampleAfterLanding() => _seekState.ConsumeJustLanded();

    /// <summary>D27-b: 回収待ちのロード解除があるか。</summary>
    public bool HasPendingFileLoadRelease => _fileLoad.HasPendingRelease;

    /// <summary>
    /// v0.5.4 段 B: 着地待ちの間は新しいシークを抑止し（門 5・10・12）、遠い新要求では着地待ちを
    /// 捨てる（門 8）。決着（着地・時間切れ・捨てた）は 1 回だけ Information に残す。
    /// </summary>
    public bool ShouldSuppressSeek(double playbackSeconds, double toleranceSeconds,
        double requestedTargetSeconds = double.NaN)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        bool suppress = _seekState.ShouldSuppressSeek(playbackSeconds, toleranceSeconds, now,
            requestedTargetSeconds);

        TimecodeSyncSeekPendingStatus status = _seekState.LastStatus;
        if (status != _lastLoggedSeekStatus &&
            status is TimecodeSyncSeekPendingStatus.Settled or TimecodeSyncSeekPendingStatus.TimedOut or
                TimecodeSyncSeekPendingStatus.Superseded)
        {
            Serilog.Log.Information(
                "Timecode sync pending {Status} playback={Playback:F3} tolerance={Tolerance:F4}",
                status, playbackSeconds, toleranceSeconds);
        }
        _lastLoggedSeekStatus = status;
        return suppress;
    }

    /// <summary>
    /// v0.5.4 段 B: 位置サンプルで着地の状態（A）を観測する。LTC のフレームの経路とは独立に、
    /// 位置を照会するすべての場所（保持の Duplicate、UI タイマー、描画の tick）から呼ぶ。
    /// v0.5.4 段 B3: 読み込み中なら、この着地の事象でロードを解除する（旧 門 18 の畳み先）。
    /// </summary>
    public void ObserveLandingState(in PlaybackPositionSample sample, double toleranceSeconds)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        _seekState.ObserveLandingSample(sample, toleranceSeconds, now);
        TryReleaseFileLoadAfterLanding(now);
    }

    /// <summary>
    /// v0.5.4 段 B3: 読み込みの着地（または安全の時間切れ）を観測したらロードを解除する。
    /// </summary>
    private void TryReleaseFileLoadAfterLanding(DateTime now)
    {
        if (!_fileLoad.IsLoadingFile)
            return;
        if (_seekState.LastStatus == TimecodeSyncSeekPendingStatus.Settled)
            ReleaseFileLoad(now, "landing");
        else if (_seekState.LandingPhase == TimecodeSyncLandingPhase.FailedToLand)
            ReleaseFileLoad(now, "timeout");
    }

    /// <summary>
    /// v0.5.4 段 B: 同じ照会の結果（<see cref="SyncPositionRead"/>）から着地の状態を観測する。
    /// 位置を照会したすべての場所が、判定の前にこれを呼ぶ。
    /// </summary>
    internal void ObserveLandingState(in SyncPositionRead read, double videoFps, double timecodeFps)
    {
        if (!read.Succeeded || read.Sample is not { } sample)
            return;
        ObserveLandingState(sample, SyncDecisionEngine.ToleranceSeconds(videoFps, timecodeFps));
    }

    /// <summary>
    /// v0.5.4 段 B2 の計測: 着地から 500ms 以内に出た同期シークを数える（門 9 の代替の物差し）。
    /// </summary>
    public void NotePostLandingSeekIssued(double targetSeconds)
        => _seekState.NotePostLandingSeekIssued(targetSeconds, _timeProvider.GetUtcNow().UtcDateTime);

    /// <summary>
    /// v0.5.4 段 B2 の計測: 着地から 500ms 以内に出た速度補正（rate.instant）を数える。
    /// </summary>
    public void NotePostLandingRateApplied(double rate)
        => _seekState.NotePostLandingRateApplied(rate, _timeProvider.GetUtcNow().UtcDateTime);

    public bool IsDebounced()
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        return (now - _lastSyncSeekAt).TotalMilliseconds < SeekDebounceMs;
    }

    /// <summary>
    /// v0.5.4 段 B3: 利用者の手動シーク（シークバー・相対・タイムライン）を着地待ちに入れる。
    /// ネイティブのシーク中は着地待ちと同じ意味（§9-7-3）。着地の観測で判定と補正を再開する。
    /// </summary>
    public void NotifyManualSeek(double targetSeconds)
        => _seekState.BeginSeek(targetSeconds, _timeProvider.GetUtcNow().UtcDateTime);

    public void ReportSeekSent(double targetSeconds)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        _lastSyncSeekAt = now;
        _latencyCompensator.MarkSeekSent();
        // v0.5.4 U4: BeginSeek が着地を待つ状態に入り、位置の信頼も落とす（門 10）。
        _seekState.BeginSeek(targetSeconds, now);
        // D37-a: シーク後は位置が飛ぶため、粗い判定のゲート履歴を切る。
        _engine.ResetSeekGate();
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
        // v0.5.4 段 B3: 開始位置つきの読み込みも、その読み込みの世代の着地待ちに入る
        // （ロードの成立（旧 門 18）はこの着地の事象で判定する）。
        _seekState.BeginLoadWait(now);
        // v0.5.3 段 3e: FileLoad はサービスの OnLifecycle の中で起きるため、外へも伝える（§6 の 5）。
        LifecycleRaised?.Invoke(SyncLifecycleEvent.FileLoad);
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
        // v0.5.4 段 B: 開始位置つきの読み込みなので、その読み込みの世代の着地待ちに入る
        // （位置は最初のフレームの配信まで使わない。18 を畳む方向。§9-7-3）。
        _seekState.BeginLoadWait(_timeProvider.GetUtcNow().UtcDateTime);
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
                // D37-a: ロードで位置が飛ぶため、粗い判定のゲート履歴を切る。
                _engine.ResetSeekGate();
                // D37-b: 素材が変わるので着地時間の学習を捨てる。保留はクリア済みなので位置は使える。
                _seekState.ResetLearning();
                _publishedSeekCostSeconds = double.NaN;
                // v0.5.4 段 B: 素材が変わるので、着地待ちと着地の記録を初期化する（§6 の 10）。
                _seekState.Clear();
                _seekState.ResetLandingState();
                // 0.4.5-A: 素材が変わるので、配信 PTS の基準と実測レートを捨てる。
                _positionFeedback.Reset();
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
        {
            // v0.5.4 段 B3: 取り消しは解除ではないので、読み込みの着地待ちも外す
            // （待ちを残すと、SyncDisabled の後は着地の観測が来ずに張り付く）。
            _seekState.Clear();
            Log.Information("Timecode sync: file load cancelled by {Event}", evt);
        }
    }

    /// <summary>
    /// v0.5.4 段 B3: ロードの成立（旧 門 18 の「再生位置と描画フレームの進み、または 5 秒」）を、
    /// 読み込みの世代の最初のフレームの配信（着地の事象）に畳んだ。着地の観測
    /// （<see cref="ObserveLandingState(in PlaybackPositionSample, double)"/>）が解除し、ここは
    /// 位置サンプルが取れない環境（旧 DLL）の安全の時間切れだけを担う。引数の進捗値は使わない。
    /// </summary>
    public bool TryMarkFileLoaded(double playbackSeconds, long renderedFrameCount)
    {
        if (!_fileLoad.IsLoadingFile) return true;
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        if (_seekState.LastStatus == TimecodeSyncSeekPendingStatus.Settled)
            return ReleaseFileLoad(now, "landing");
        if (now - _fileLoad.StartedAt >= TimecodeSyncSeekState.LandingSafetyTimeout)
        {
            _seekState.Clear();
            return ReleaseFileLoad(now, "timeout");
        }

        return false;
    }

    private bool ReleaseFileLoad(DateTime now, string reason)
    {
        double loadElapsedMs = _fileLoad.StartedAt == DateTime.MinValue
            ? 0.0
            : (now - _fileLoad.StartedAt).TotalMilliseconds;
        _fileLoad.Release(now);
        _lastSyncSeekAt = now;                // ロード後デバウンスを再スタート
        if (reason == "landing")
            // v0.5.4 段 B3: 着地の事象によるロード解除（門 18 の通常経路）。
            Serilog.Log.Debug("sync.gate load-release elapsedMs={ElapsedMs:F1}", loadElapsedMs);
        else
            Serilog.Log.Information("Timecode sync: file load released ({Reason})", reason);
        return true;
    }

    public void ClearSeekState()
    {
        // D37-a: 保留の破棄・手動移動の後はゲートの系列を切る。
        _engine.ResetSeekGate();
        // v0.5.4 段 B: 保留を外から破棄したので、着地待ちと着地の記録も初期化する。
        bool loading = _fileLoad.IsLoadingFile;
        _seekState.Clear();
        _seekState.ResetLandingState();
        // v0.5.4 段 B3: 読み込みの着地待ちはロードの成立（旧 門 18）が握っているので、
        // モード変更・手動移動で消さない（消すとロードの解除が安全の時間切れまで残る）。
        if (loading)
            _seekState.BeginLoadWait(_timeProvider.GetUtcNow().UtcDateTime);
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

    /// <summary>
    /// v0.5.4 段 B: 遠い新要求の置き換えのシーク（門 8）。13 のゲートと 15 の速度補正を通さず、
    /// その場で出す決定を作る。
    /// </summary>
    private static SyncDecision ReplacementSeekDecision(
        double targetSeconds, SyncPlaybackState state, SyncDecision untrusted) => new(
        SyncActionType.Seek, targetSeconds, targetSeconds - state.PlaybackSeconds,
        untrusted.ToleranceSeconds, untrusted.VideoFpsUsed, untrusted.TimecodeFpsUsed,
        untrusted.UsedDefaultVideoFps, untrusted.UsedDefaultTimecodeFps);

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
        // 位置を判定に使わない状態か（シークの着地待ち・取り直し待ち。v0.5.4 U4: A の状態）。
        ["positionUntrusted"] = !_seekState.IsPositionUsable,
    };
}
