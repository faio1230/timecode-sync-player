using System.Diagnostics;
using Serilog;

namespace TimecodeSyncPlayer;

internal readonly record struct LtcSyncContext(
    bool IsPlayerReady,
    bool SyncEnabled,
    SyncMode Mode,
    bool IsSeeking,
    bool IsMonitoring,
    bool IsPlaybackPaused,
    LtcSignalLossMode SignalLossMode,
    TimecodeFpsMode FpsMode,
    GapBehavior GapBehavior,
    Guid? LoadedTrackId,
    double VideoFps,
    double DurationSeconds,
    int SignalLossTimeoutMilliseconds,
    int SignalResumeFrames,
    // D33: Single の補正（Jump の目標・残差）を D29 と同じ [MediaIn, MediaOut ?? 尺] に
    // 収めるための範囲。Continue では使わない。
    double MediaInSeconds = 0.0,
    double? MediaOutSeconds = null);

/// <summary>UI/native I/O boundaries. All LTC routing and state transitions belong to the controller.</summary>
internal sealed record LtcSyncEffects(
    Func<LtcSyncContext> GetContext,
    Action<string, string> ApplyFrameText,
    Action<LtcDisplayState, string> ApplyDisplay,
    Action<bool> SetMonitoring,
    Action<bool> SetSignalLossPaused,
    Action ResumeProjectRestorePause,
    Action ClearGapFreezeFrame,
    Action RefreshCurrentVideoFrame,
    Action<double> UpdateTimelinePosition,
    Action UpdateCurrentTrackLabel,
    Action ResumeGapPause,
    Func<SyncCorrectionMode>? GetCorrectionMode = null,
    Func<double?>? GetPlaybackSeconds = null,
    Func<long>? GetTotalRenderedFrames = null,
    Func<double, bool>? ApplyRateInstant = null,
    Func<double, bool>? SeekTo = null,
    Action<string>? SetCorrectionStatus = null,
    Func<double>? GetSyncOffsetMilliseconds = null,
    // 0.4.8: 直近に再生位置が後退した（復号が追いつかずパイプライン位置が 2 系列を行き来する）か。
    Func<bool>? IsPlaybackPositionUnstable = null,
    // v0.5.3 段 3i: 信号断のポリシー以外の一時停止の持ち主（境界ホールド・ギャップ・
    // プロジェクト復元）。ポリシーの一時停止を解いたときの再開判定に使う。
    Func<PauseOwners>? GetOtherPauseOwners = null,
    // v0.5.4 段 B1: 着地の状態（新しい判定）の観測用。位置を照会するすべての場所から呼ぶ
    // （UI タイマー・保持の Duplicate）。サンプルが取れない環境では null のままでよい。
    Func<SyncPositionRead>? ReadPosition = null);

/// <summary>
/// UI-thread LTC session orchestration shared by the window and integration scenarios.
/// Receive timestamps are captured by the audio callback before dispatching to this class.
/// Construction only stores dependencies; it does not access UI controls.
/// </summary>
internal sealed class LtcSyncController
{
    /// <summary>T2: サンプル時計（フレーム終端から受信ハンドラまでの経過を同期値に足す）の切替。既定 on。</summary>
    internal const string SampleClockEnvironmentVariable = "TCS_LTC_SAMPLE_CLOCK";

    /// <summary>T2: age として受け付ける上限。停止や時計の不一致を同期値へ持ち込まない。</summary>
    internal const double MaxSampleClockAgeSeconds = 0.5;

    /// <summary>U1: 再適用（GapBehaviorChanged 等）からの age 計算であることを示す発生元。</summary>
    private const string ReapplyAgeSource = "reapply";

    private readonly PlaylistState _playlist;
    private readonly GapFreezeHandler _gap;
    private readonly TimecodeSyncService _syncService;
    private readonly LtcFrameProcessor _frames;
    private readonly LtcSignalLossPolicy _signalLoss;
    private readonly LtcSignalLossMonitoringState _monitoring = new();
    private readonly LtcSyncEffects _effects;
    private readonly Func<SingleModeSyncCoordinator> _single;
    private readonly Func<ContinueOnTrackCoordinator> _continue;
    private readonly Func<GapEnterCoordinator> _gapCoordinator;
    private readonly ContinueModeQueryLogState _queryLog = new(TimeSpan.FromSeconds(1), mediaPositionToleranceSeconds: 0.5);
    private readonly SyncCorrectionController _correction = new();
    private readonly Func<DateTime> _getUtcNow;
    private readonly bool _sampleClockEnabled;
    private readonly Func<long> _getQpc;
    private ContinueFrameContext? _lastContinueFrame;
    /// <summary>v0.5.2 段 2d: 速度補正の軸の状態。</summary>
    private readonly RateCorrectionState _rate = new();
    /// <summary>v0.5.2 段 2b: 入力（LTC）の軸の状態。</summary>
    private readonly LtcInputState _input = new();
    private bool _sampleClockAgeWarned;
    private string _formatText = "LTC 停止中";

    public LtcSyncController(
        PlaylistState playlist, GapFreezeHandler gap, TimecodeSyncService syncService,
        LtcFrameProcessor frames, int timeoutMilliseconds, int resumeFrames,
        LtcSyncEffects effects, Func<SingleModeSyncCoordinator> single,
        Func<ContinueOnTrackCoordinator> continueOnTrack, Func<GapEnterCoordinator> gapCoordinator,
        Func<DateTime>? getUtcNow = null,
        bool? sampleClockEnabled = null,
        Func<long>? getQpc = null)
    {
        _playlist = playlist;
        _gap = gap;
        _syncService = syncService;
        _frames = frames;
        _signalLoss = new(TimeSpan.FromMilliseconds(timeoutMilliseconds), resumeFrames);
        _effects = effects;
        _single = single;
        _continue = continueOnTrack;
        _gapCoordinator = gapCoordinator;
        _getUtcNow = getUtcNow ?? (() => DateTime.UtcNow);
        _sampleClockEnabled = sampleClockEnabled ?? IsSampleClockEnabled(
            Environment.GetEnvironmentVariable(SampleClockEnvironmentVariable));
        _getQpc = getQpc ?? Stopwatch.GetTimestamp;
        Log.Information(
            "LTC sample clock: {State}（{Variable}=off のときだけ無効）",
            _sampleClockEnabled ? "有効" : "無効", SampleClockEnvironmentVariable);
        _syncService.SeekIssued += OnSeekIssued;
        // v0.5.4 B6b（規則 1・3）: マスターが止まっている（保持の Duplicate・信号断）間は、relocate の
        // 目標に先行量を付けない（停止した値へ合わせる。D37-g の守り）。
        // v0.5.4（マスター停止の判定の共通化）: 保持は数える保持の連続 1 枚以上（fps の疑わしい Duplicate は
        // 数えない）。保持値の記録（D27-d、化けた 1 枚でも立つ）では判定しない。
        _syncService.MasterStoppedSource = () =>
            SyncRules.IsMasterStopped(_input.HeldRunLength, minimumHeldFrames: 1) || _signalLoss.IsLost;
        _syncService.LifecycleRaised += OnSyncServiceLifecycle;
    }

    /// <summary>
    /// v0.5.3 段 3e: サービスのできごとで、保持値の 1 回適用のラッチを下ろす（§6 の 5）。
    /// BeginFileLoad の FileLoad はサービスの OnLifecycle の中で起き、コントローラの
    /// OnLifecycle には届かないため、購読して受け取る。
    /// </summary>
    private void OnSyncServiceLifecycle(SyncLifecycleEvent evt)
    {
        if (evt != SyncLifecycleEvent.FileLoad)
            return;
        _input.ClearHeldReapplied();
        // v0.5.4 K5（§6 の 6）: 読み込みで Smooth を再試行できるようにする。
        _rate.ResetSmoothAvailability();
        // v0.5.4 K5（§6 の 1）: 読み込みで Single の境界ホールドを解除する（解除の副作用つき）。
        _single().OnLifecycle(SyncLifecycleEvent.FileLoad);
        PauseOnFileLoadDuringLoss();
    }

    /// <summary>
    /// v0.5.4（規則 4 の読み込みの入口）: 停止モードで損失のまま読み込んだら、読み込みが始めた再生を
    /// 規則 4 の入口と同じく一時停止する（持ち主 = 信号断）。新しいトラックでも保持値へ 1 回着地するよう
    /// 保持着地の記録を下ろし、着地は読み込みが着地した後の保持のフレーム（D35 の経路、尺と fps が
    /// 分かってから）に任せる。復帰は新しい値のフレームで今どおり（規則 2〜3）。
    /// </summary>
    private void PauseOnFileLoadDuringLoss()
    {
        if (_signalLoss.OnFileLoad(SignalContext()) != LtcSignalLossAction.Pause)
            return;
        _input.ClearHeldLossLanding();
        ApplySignalLossAction(LtcSignalLossAction.Pause, landOnHeldValue: false);
    }

    public double LastLtcSeconds { get; private set; }
    public double LastTimecodeFps => _frames.LastTimecodeFps;

    internal bool IsSignalLossPauseOwned => _signalLoss.IsPauseOwned;

    /// <summary>D37-c: 速度補正の入力から弾いた標本の累計（計測・テスト用）。</summary>
    internal long CorrectionRejectedSamples => _rate.RejectedSamples;

    /// <summary>
    /// 環境変数の解釈（T2 段 3: 既定 on）。明示的な off（大文字小文字不問）のときだけ無効。
    /// </summary>
    internal static bool IsSampleClockEnabled(string? value)
        => value is null || !value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase);

    /// <summary>テスト・診断用: 直近フレームの Continue 補正文脈（フレーム先頭で捨てる）。</summary>
    internal ContinueFrameContext? LastContinueFrame => _lastContinueFrame;

    public void SyncEnabledChanged()
    {
        SyncLifecycleEvent evt = _effects.GetContext().SyncEnabled
            ? SyncLifecycleEvent.SyncEnabled
            : SyncLifecycleEvent.SyncDisabled;
        SyncLifecycle.Record(evt, nameof(SyncEnabledChanged));
        OnLifecycle(evt);
        _syncService.OnLifecycle(evt);
        // v0.5.3 段 3c: 同期の無効化で Single の境界ホールドのラッチを消す（§6 の 1）。
        if (evt == SyncLifecycleEvent.SyncDisabled)
            _single().OnLifecycle(evt);
        ExitGapForManualControl();
        ReapplyLastAcceptedFrame();
    }

    public void SyncModeChanged()
    {
        SyncLifecycle.Record(SyncLifecycleEvent.SyncModeChanged, nameof(SyncModeChanged));
        OnLifecycle(SyncLifecycleEvent.SyncModeChanged);
        _syncService.OnLifecycle(SyncLifecycleEvent.SyncModeChanged);
        // v0.5.3 段 3c: モード切替で Single の境界ホールドのラッチを消す（§6 の 1）。
        _single().OnLifecycle(SyncLifecycleEvent.SyncModeChanged);
        ExitGapForManualControl();
        _effects.UpdateCurrentTrackLabel();
        ReapplyLastAcceptedFrame();
    }

    /// <summary>v0.5.3 段 3h: 補正モードの変更で倍率を 1.0 に戻す（§6 の 8）。</summary>
    public void CorrectionModeChanged()
    {
        SyncLifecycle.Record(SyncLifecycleEvent.CorrectionModeChanged, nameof(CorrectionModeChanged));
        OnLifecycle(SyncLifecycleEvent.CorrectionModeChanged);
    }

    /// <summary>
    /// v0.5.3 段 3i: 信号断モードの変更（§6 の 9）。ランスルーへ変えてポリシーの一時停止を
    /// 解いたときだけ、ほかの持ち主がいなければ再生を再開する。
    /// </summary>
    public void SignalLossModeChanged()
    {
        SyncLifecycle.Record(SyncLifecycleEvent.SignalLossModeChanged, nameof(SignalLossModeChanged));
        OnLifecycle(SyncLifecycleEvent.SignalLossModeChanged);
        if (!_signalLoss.OnSignalLossModeChanged(_effects.GetContext().SignalLossMode))
            return;
        PauseOwners otherOwners = _effects.GetOtherPauseOwners?.Invoke() ?? PauseOwners.None;
        if (!SyncRules.ShouldResumeOnPolicyPauseRelease(otherOwners))
        {
            Log.Information(
                "LTC signal loss mode changed: policy pause released, playback stays paused owners={Owners}",
                otherOwners);
            return;
        }
        _effects.SetSignalLossPaused(false);
        Log.Information("LTC signal loss mode changed: policy pause released, playback resumed");
    }

    /// <summary>
    /// v0.5.2 段 1: できごとでこのクラスのラッチを消す入口。段 0 の寿命の表の「現状」の列どおりに消す
    /// （各分岐は段 1 の前に各入口メソッドにあった処理を、順番を変えずに移したもの）。
    /// フレームの中で消えるもの（Normal フレーム、Continue のトラック切替、ギャップのフレーム、
    /// 補正評価）はフレーム経路のまま。
    /// </summary>
    private void OnLifecycle(SyncLifecycleEvent evt)
    {
        switch (evt)
        {
            case SyncLifecycleEvent.SyncEnabled:
                ResetCorrection();
                _rate.ResetSmoothAvailability();
                // v0.5.3 段 3e: 保持値の 1 回適用のラッチを下ろす（§6 の 5）。
                _input.ClearHeldReapplied();
                break;
            case SyncLifecycleEvent.SyncDisabled:
                ResetCorrection();
                // v0.5.4 K5（§6 の 7）: 無効化の時点で戻せなくても、復帰待ちを残さない。
                RetryRateRestoreIfPending();
                _rate.ResetSmoothAvailability();
                break;
            case SyncLifecycleEvent.SyncModeChanged:
                ResetCorrection();
                _rate.ResetSmoothAvailability();
                _frames.ResetDiagnostics();
                _input.DiscardPendingJump();
                // v0.5.3 段 3e: 保持値の 1 回適用のラッチを下ろす（§6 の 5）。
                _input.ClearHeldReapplied();
                break;
            case SyncLifecycleEvent.ManualSeek:
            case SyncLifecycleEvent.TimelineSeek:
                _input.DiscardPendingSync();
                _input.DiscardPendingJump();
                // v0.5.3 段 3e: 保持値の 1 回適用のラッチを下ろす（§6 の 5）。
                _input.ClearHeldReapplied();
                // T7: 手動シークは補正状態（Smooth の無効化を含む）も捨てる。
                ResetCorrection();
                // v0.5.3 段 3h: 手動シークで Smooth 不可を戻す（§6 の 13、利用者決定 2026-09-25）。
                _rate.ResetSmoothAvailability();
                break;
            case SyncLifecycleEvent.PlaybackStopped:
                // T7: 停止・プロジェクト差し替えで補正状態を捨てる。
                ResetCorrection();
                break;
            case SyncLifecycleEvent.PlayPauseToggled:
                // T7: 操作者の再生・一時停止で補正状態を捨てる。
                ResetCorrection();
                // v0.5.4 K5（§6 の 7）: 一時停止中に戻せなかった保留を、操作のたびに戻しにいく。
                RetryRateRestoreIfPending();
                break;
            case SyncLifecycleEvent.CorrectionModeChanged:
                // v0.5.3 段 3h: 補正モードの変更で倍率を 1.0 に戻す（§6 の 8）。
                // 戻せないときは ResetCorrection が復帰待ちにする（次の評価で戻す）。
                ResetCorrection();
                break;
            case SyncLifecycleEvent.FpsModeChanged:
                _input.DiscardPendingJump();
                _frames.ResetForFpsMode(_effects.GetContext().FpsMode);
                break;
            case SyncLifecycleEvent.MonitoringStarted:
                _input.ClearFrameHistory();
                _monitoring.MarkStarted();
                _signalLoss.OnLifecycle(evt);
                break;
            case SyncLifecycleEvent.MonitoringStopped:
                // v0.5.3 段 3h: 監視の停止で倍率を 1.0 に戻す（§6 の 14、利用者決定 2026-09-25）。
                ResetCorrection();
                _input.ClearFrameHistory();
                if (!_monitoring.IsDetectionActive(isReportedRunning: false))
                    _signalLoss.OnLifecycle(evt);
                break;
            case SyncLifecycleEvent.MonitorDeviceStopped:
                // 信号断のポリシーの初期化は、正常な停止のときだけ入口（MonitorStopped）が行う。
                // v0.5.3 段 3h: 監視の停止で倍率を 1.0 に戻す（§6 の 14、利用者決定 2026-09-25）。
                ResetCorrection();
                _input.ClearFrameHistory();
                break;
            case SyncLifecycleEvent.BoundaryHoldReleased:
                // D35-b: ホールド中に残った端への保留シークと保持着地のラッチを解除する。
                _input.ClearHeldLossLanding();
                _input.ClearHeldReapplied();
                _input.DiscardPendingSync();
                // v0.5.4 段 B: 保留を外から破棄したので、着地待ちと着地の記録を初期化する。
                _syncService.SeekState.ResetLandingState();
                break;
        }
    }

    public void GapBehaviorChanged()
    {
        // U1 計測: コンボ変更ハンドラから同期で入る再適用（age 警告の発生元になり得る）。
        long started = Stopwatch.GetTimestamp();
        ReapplyLastAcceptedFrame();
        Log.Debug("Gap behavior reapply: elapsedMs={ElapsedMs:F1}",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private void ReapplyLastAcceptedFrame()
    {
        _input.DiscardPendingSync();
        LtcSyncContext state = _effects.GetContext();
        LtcInputState.AcceptedFrame? acceptedOrNull = _input.Accepted;
        if (!SyncRules.CanReapplyLastAccepted(
                acceptedOrNull.HasValue, state.IsMonitoring,
                state.SyncEnabled, state.IsSeeking, _signalLoss.ShouldSuppressSync))
            return;
        LtcInputState.AcceptedFrame accepted = acceptedOrNull.GetValueOrDefault();

        bool stale = IsStaleReapply(accepted.FrameEndTimestamp);
        if (_sampleClockEnabled && accepted.FrameEndTimestamp > 0)
            Log.Debug("LTC sample clock: reapply ageMs={AgeMs:F1} deferred={Deferred}",
                ReapplyAgeMilliseconds(accepted.FrameEndTimestamp), stale);

        if (stale)
        {
            // U1: 最後のフレーム終端から 0.5 秒より古い再適用では同期要求（シーク目標）を
            // 出さず、次の有効フレームに任せる。ギャップ表示の切替は ApplySync の
            // ギャップ分岐が即時に行う（gapDisplayOnly）。
            ApplySync(EffectiveSeconds(accepted.RawSeconds, accepted.FrameEndTimestamp, ReapplyAgeSource),
                gapDisplayOnly: true);
            return;
        }

        RequestSync(accepted.RawSeconds, accepted.FrameEndTimestamp, ReapplyAgeSource);
    }

    /// <summary>
    /// U1: 再適用時点で最後のフレーム終端が 0.5 秒より古い（停止前の値である）か。
    /// サンプル時計 off では age を使わないため常に false。
    /// </summary>
    private bool IsStaleReapply(long frameEndTimestamp)
    {
        if (!_sampleClockEnabled || frameEndTimestamp <= 0)
            return false;
        double ageSeconds = (_getQpc() - frameEndTimestamp) / (double)Stopwatch.Frequency;
        return ageSeconds is < 0 or > MaxSampleClockAgeSeconds;
    }

    private double ReapplyAgeMilliseconds(long frameEndTimestamp) =>
        frameEndTimestamp <= 0
            ? 0.0
            : (_getQpc() - frameEndTimestamp) * 1000.0 / Stopwatch.Frequency;

    /// <summary>手動シーク（シークバー・相対シーク）。source はログに残す呼び出し元。</summary>
    public void CancelPendingSync(string source = "manual")
    {
        SyncLifecycle.Record(SyncLifecycleEvent.ManualSeek, source);
        OnLifecycle(SyncLifecycleEvent.ManualSeek);
    }

    /// <summary>タイムラインのクリック。手動シークに加えて、同期側のシークの保留状態も捨てる。</summary>
    public void TimelineSeek()
    {
        SyncLifecycle.Record(SyncLifecycleEvent.TimelineSeek, nameof(TimelineSeek));
        OnLifecycle(SyncLifecycleEvent.TimelineSeek);
        _syncService.OnLifecycle(SyncLifecycleEvent.TimelineSeek);
    }

    /// <summary>
    /// <summary>
    /// T9/B6b-16/23: 粗い同期シークの発行。速度補正の残差の系列を切る（粗い判定の ResetSeekGate と
    /// 同じ考え方）。着地直後の上限窓（±0.20）は畳んだ（着地直後の 1 サンプルは varispeed しない）。
    /// </summary>
    private void OnSeekIssued()
    {
        _rate.ResetResidualGate();
        _rate.ClearRejectedLogged();
        // v0.5.4 B6b（規則 3、TSP-Fable の判定）: relocate は varispeed を持ち越さない。どの経路の relocate
        // （同期・ギャップの出口・保持の着地・保持の入口・Jump の補正）でも、発行した時点で位置の不安定による
        // 補正の停止を下ろし、倍率を 1.0 に戻す（戻せなければ既存どおり復帰待ちにする）。
        if (_rate.ExitPositionPause())
            Log.Information("Smooth correction resumed: relocate issued");
        if (_rate.RateRestorePending || _rate.RateNotUnity)
            TryRestoreRateToUnity();
    }

    /// <summary>T7: 再生の停止（プロジェクト・プレイリストの差し替えを含む）で補正状態を捨てる。</summary>
    public void PlaybackStopped()
    {
        SyncLifecycle.Record(SyncLifecycleEvent.PlaybackStopped, nameof(PlaybackStopped));
        OnLifecycle(SyncLifecycleEvent.PlaybackStopped);
        // v0.5.3 段 3d: 停止でロード中の印と解除の回収待ちを取り消す（§6 の 3）。
        _syncService.OnLifecycle(SyncLifecycleEvent.PlaybackStopped);
        // v0.5.3 段 3c: 再生の停止で Single の境界ホールドのラッチを消す（§6 の 1）。
        _single().OnLifecycle(SyncLifecycleEvent.PlaybackStopped);
    }

    /// <summary>T7: 操作者の再生・一時停止で補正状態を捨てる。</summary>
    public void PlayPauseToggled()
    {
        SyncLifecycle.Record(SyncLifecycleEvent.PlayPauseToggled, nameof(PlayPauseToggled));
        OnLifecycle(SyncLifecycleEvent.PlayPauseToggled);
    }

    /// <summary>
    /// T7: 補正状態を捨て、プレイヤーに掛けた倍率が残っていれば 1.0 に戻す。
    /// 一時停止中などで戻せないときは次の評価可能フレームの評価前に戻す。
    /// </summary>
    private void ResetCorrection()
    {
        _correction.Reset();
        // D37-c: 補正の入力系列も一緒に切る（前の系列の中央値・変化量を混ぜない）。
        _rate.ResetResidualGate();
        _rate.ClearRejectedLogged();
        if (_rate.RateRestorePending || !_rate.RateNotUnity)
            return;
        TryRestoreRateToUnity();
    }

    /// <summary>倍率を 1.0 へ戻す。戻せなかったら復帰待ちにする。</summary>
    private void TryRestoreRateToUnity()
    {
        if (_effects.ApplyRateInstant?.Invoke(1.0) == true)
            _rate.MarkRestored();
        else
            _rate.MarkRestorePending();
    }

    /// <summary>
    /// v0.5.4 K5（§6 の 7）: 復帰待ちの保留を 1.0 へ戻すことを試す（戻せたら下ろす）。
    /// 同期の無効化・一時停止のあとの操作で、速度が 1.0 に戻らない保留を残さない。
    /// </summary>
    private void RetryRateRestoreIfPending()
    {
        if (!_rate.RateRestorePending)
            return;
        if (_effects.ApplyRateInstant?.Invoke(1.0) == true)
            _rate.MarkRestored();
    }

    private void RequestSync(double rawSeconds, long frameEndTimestamp, string source = "frame")
        => ApplySyncRequest(EffectiveSeconds(rawSeconds, frameEndTimestamp, source), rawSeconds, frameEndTimestamp);

    private void RequestSyncEffective(double effectiveSeconds)
        => ApplySyncRequest(effectiveSeconds, pendingRawSeconds: 0, pendingFrameEndTimestamp: 0);

    /// <summary>
    /// 同期要求を 1 回評価し、Deferred なら再送用に「評価した値」を丸ごと保持する。
    /// 生値・フレーム終端はフレーム由来の要求だけが持ち、Tick の再送はそれがあるときだけ
    /// age を取り直す（Jump・保持・再適用の要求を、古いフレームの生値で上書きしない）。
    /// D37-a: ゲートが Seek を保留している間も、この保留が正しい値で再送される。
    /// </summary>
    private void ApplySyncRequest(double effectiveSeconds, double pendingRawSeconds, long pendingFrameEndTimestamp)
    {
        // U1 計測: コンボ変更・フレーム受信からギャップ状態再評価までの所要。
        long started = Stopwatch.GetTimestamp();
        SyncRequestResult result = ApplySync(effectiveSeconds);
        if (result == SyncRequestResult.Deferred)
        {
            _input.HoldPendingSync(effectiveSeconds, pendingRawSeconds, pendingFrameEndTimestamp);
        }
        else
        {
            _input.DiscardPendingSync();
        }

        Log.Debug("sync.apply: elapsedMs={ElapsedMs:F1} result={Result} ltc={Ltc:F3}",
            Stopwatch.GetElapsedTime(started).TotalMilliseconds, result, effectiveSeconds);
    }

    /// <summary>
    /// T2: 同期に使う値。サンプル時計が有効なら、フレーム終端からここまでの経過（age）を
    /// 生の LTC 秒に足してから、T3 のオフセットを 1 回だけ適用する。
    /// </summary>
    private double EffectiveSeconds(double rawSeconds, long frameEndTimestamp, string source)
    {
        double seconds = rawSeconds + SampleClockAgeSeconds(frameEndTimestamp, source);
        return SyncOffsetPolicy.Apply(
            seconds, _effects.GetSyncOffsetMilliseconds?.Invoke() ?? SyncOffsetPolicy.DefaultMilliseconds);
    }

    /// <summary>
    /// フレーム終端からハンドラが動くまでの経過。0〜0.5 秒の外は足さず、1 回だけ警告する
    /// （時計の不一致や停止の取り違えを同期値へ持ち込まない）。U1: 再適用の age は
    /// 「信号停止前に受けたフレームの古さ」でフレーム経路の遅延ではないため、警告は
    /// frame/tick のときだけに使い、再適用では消費しない。
    /// </summary>
    private double SampleClockAgeSeconds(long frameEndTimestamp, string source)
    {
        if (!_sampleClockEnabled || frameEndTimestamp <= 0)
            return 0.0;
        double age = (_getQpc() - frameEndTimestamp) / (double)Stopwatch.Frequency;
        if (age is < 0 or > MaxSampleClockAgeSeconds)
        {
            if (!_sampleClockAgeWarned && !string.Equals(source, ReapplyAgeSource, StringComparison.Ordinal))
            {
                _sampleClockAgeWarned = true;
                Log.Warning(
                    "LTC sample clock: age={AgeMs:F1}ms は範囲外（0〜{MaxMs:F0}ms）のため同期値に足しません source={Source}",
                    age * 1000.0, MaxSampleClockAgeSeconds * 1000.0, source);
            }
            return 0.0;
        }
        return age;
    }

    public void FpsModeChanged()
    {
        SyncLifecycle.Record(SyncLifecycleEvent.FpsModeChanged, nameof(FpsModeChanged));
        OnLifecycle(SyncLifecycleEvent.FpsModeChanged);
    }

    public void MonitoringChanged()
    {
        bool monitoring = _effects.GetContext().IsMonitoring;
        SyncLifecycleEvent evt = monitoring
            ? SyncLifecycleEvent.MonitoringStarted
            : SyncLifecycleEvent.MonitoringStopped;
        SyncLifecycle.Record(evt, nameof(MonitoringChanged));
        OnLifecycle(evt);
        if (monitoring)
            _formatText = "fps: 検出中...";
        else if (!_monitoring.IsDetectionActive(isReportedRunning: false))
            _formatText = "LTC 停止中";
        RefreshDisplay();
    }

    public void DeviceEnumerationFailed()
    {
        _formatText = "LTC デバイス列挙失敗";
        RefreshDisplay();
    }

    public void MonitorStopped(Exception? exception)
    {
        SyncLifecycle.Record(SyncLifecycleEvent.MonitorDeviceStopped, exception == null ? "stopped" : "error");
        OnLifecycle(SyncLifecycleEvent.MonitorDeviceStopped);
        if (_monitoring.MarkStopped(exception))
        {
            _signalLoss.OnLifecycle(SyncLifecycleEvent.MonitorDeviceStopped);
            _effects.ApplyFrameText("--:--:--:--", "-.--- s");
        }
        _formatText = exception == null ? "LTC 停止中" : "LTC 停止エラー";
        // MarkStopped must precede this effect: the VM synchronously re-enters MonitoringChanged.
        _effects.SetMonitoring(false);
        RefreshDisplay();
    }

    public void ReceiveFrame(LtcFrameReceivedEventArgs frame, long receivedAtMilliseconds)
    {
        TimecodeFpsMode mode = _effects.GetContext().FpsMode;
        ReceiveProcessedFrame(_frames.Process(frame, mode), receivedAtMilliseconds, frame, mode);
    }

    /// <summary>Accept an already processed frame, retaining its diagnostic gate and display state.</summary>
    public void ReceiveProcessedFrame(LtcFrameProcessingResult processed, long receivedAtMilliseconds) =>
        ReceiveProcessedFrame(processed, receivedAtMilliseconds, sourceFrame: null, _effects.GetContext().FpsMode);

    private void ReceiveProcessedFrame(
        LtcFrameProcessingResult processed, long receivedAtMilliseconds,
        LtcFrameReceivedEventArgs? sourceFrame, TimecodeFpsMode mode)
    {
        ApplyFrame(processed);
        if (sourceFrame != null)
            LogFrameDiagnostics(sourceFrame, processed, mode);
        double rawSeconds = processed.ResolvedSeconds;
        long frameEndTimestamp = sourceFrame?.FrameEndTimestamp ?? 0;
        // v0.5.4（規則 4 の入口の数え方）: 入口に数える保持の連続を、すべてのフレームで数える。Jump の保留・
        // 別の値・fps の疑わしい Duplicate が挟まったら数え直す（RunThrough の入口と停止モードの U8 で共有）。
        int heldRun = _input.ObserveHeldRun(
            IsCountedHeldFrame(processed, sourceFrame, mode) ? rawSeconds : null,
            (LastTimecodeFps > 0 ? 1.0 / LastTimecodeFps : 0.04) * 0.5);
        // D30: 未確認 Jump の確認。直後の 1 フレームが同値の Duplicate か +1 フレームなら、
        // その値を確認済み Jump として適用する（保持損失からの復帰も確認後に行う）。
        if (_input.PendingJumpSeconds is double pendingJump)
        {
            _input.ClearPendingJumpSeconds();
            bool withinWindow = JumpConfirmationPolicy.IsWithinConfirmationWindow(
                _input.PendingJumpFrameEndTimestamp, frameEndTimestamp,
                _input.PendingJumpReceivedAt, receivedAtMilliseconds, LastTimecodeFps);
            if (withinWindow &&
                JumpConfirmationPolicy.IsConfirmedBy(
                    pendingJump, rawSeconds, LastTimecodeFps, processed.Diagnostic.Status))
            {
                ApplyConfirmedJump(processed.Diagnostic.Status, rawSeconds, frameEndTimestamp, receivedAtMilliseconds, heldRun);
                return;
            }
            if (!withinWindow)
            {
                // D31: 窓はサンプル時計（FrameEndTimestamp）優先。壁時計（受信時刻）は参考値として出す。
                Log.Information(
                    "Timecode sync: dropping out-of-window pending Jump frame ltc={Ltc:F3} next={Next:F3} streamMs={StreamMs:F1} wallMs={WallMs}",
                    pendingJump, rawSeconds,
                    JumpConfirmationPolicy.SampleClockDifferenceMilliseconds(
                        _input.PendingJumpFrameEndTimestamp, frameEndTimestamp) ?? -1.0,
                    receivedAtMilliseconds - _input.PendingJumpReceivedAt);
            }
        }

        bool applyOnce;
        string applyReason;
        if (!processed.ShouldApplySync)
        {
            bool heldValueChangedDuringLoss = false;
            // v0.5.4 B6b（規則 4）: マスター停止（保持）の入口。同じ値のフレームが続いた 2 枚目の
            // Duplicate（直前も保持）で、この保持でまだ合わせていない（保持着地の記録が無い）とき。
            // v0.5.4（入口の数え方）: 2 枚は連続した同値で、fps の疑わしいものは数えない（heldRun）。
            bool holdEntry = false;
            // D27: 解読は続いているが値が進まない保持（Duplicate）を信号停止の判定へ伝える。
            // 無音（フレームが届かない）と同じ経路で損失になり、損失の理由だけが分かれる。
            // D27-d: 停止時の着地目標に使う「保持として届いた値」もここで記録する
            // （保持直前の受理値は 1 フレーム手前になり得る）。
            if (processed.Diagnostic.Status == TimecodeFrameDiagnosticStatus.Duplicate)
            {
                _signalLoss.ObserveHeldFrame(receivedAtMilliseconds, SignalContext(), heldRun);
                // D27-d: 着地目標は保持として届いた値そのもの。保持値は凍結されて進まないため、
                // サンプル時計の age は足さず T3 オフセットだけ適用する。
                double heldEffectiveSeconds = SyncOffsetPolicy.Apply(rawSeconds,
                    _effects.GetSyncOffsetMilliseconds?.Invoke() ?? SyncOffsetPolicy.DefaultMilliseconds);
                // D31-b: 損失中の保持値の変化は、着地済みの値（無ければ直前の保持値）と比べる。
                heldValueChangedDuringLoss = IsHeldValueChangedDuringLoss(heldEffectiveSeconds);
                holdEntry = _input.LastHeldEffectiveSeconds is not null &&
                    SyncRules.IsMasterStopped(heldRun, minimumHeldFrames: 2) &&
                    _input.HeldLossLandingSeconds is null;
                _input.MarkHeldEffective(heldEffectiveSeconds);
                // D38 (a): 保持の Duplicate でも、保留中のシークが着地していれば観測して
                // 位置の信頼を戻す（シークは出さない）。
                ObservePendingSeekLanding();
                // D33: 保持（Duplicate）では通常の同期評価が走らない。範囲外 LTC の保持中でも
                // 終端ホールド／解除を評価する（境界へのシークは通常フレーム側が行う）。
                LtcSyncContext heldState = _effects.GetContext();
                if (heldState.Mode == SyncMode.Single && heldState.SyncEnabled && heldState.IsMonitoring)
                    _single().ApplyClipBoundaryHoldOnly(heldEffectiveSeconds);
            }
            // v0.5.4 B7（Jump の確認の一様化、§10-0）: Jump はすべて未確認にして、次の 1 フレームの
            // 値の連続性（同値の Duplicate か +1 フレーム）だけで確かめる（誤値 1 枚で状態を動かさない。D30）。
            // 写像（ギャップ・別トラック・範囲外）や保持損失中かどうかで分けない。遅れは 1 フレームで一律。
            // 保持損失中の Jump の復帰（D27-b/c）も、確認した後に ApplyConfirmedJump が行う。
            if (processed.Diagnostic.Status == TimecodeFrameDiagnosticStatus.Jump)
            {
                string deferReason = UnconfirmedJumpReason(processed, sourceFrame);
                _input.HoldPendingJump(rawSeconds, receivedAtMilliseconds, frameEndTimestamp);
                Log.Information(
                    "Timecode sync: holding unconfirmed Jump frame ltc={Ltc:F3} reason={Reason}",
                    rawSeconds, deferReason);
                return;
            }
            // D31-b: 保持損失中に保持値そのもの（タイムコード停止位置）が変わったら、停止モードは
            // 新しい保持値へ 1 回だけ着地する（D27 の着地を遷移時から変化時へ拡張）。ランスルーは
            // 同期の 1 回適用に同じ変化の判定を使う（同値の連続では発行しない）。
            // D35: 無音損失で一時停止した後に初めて保持値が届いた場合も、同じ明示着地の対象にする。
            else if (processed.Diagnostic.Status == TimecodeFrameDiagnosticStatus.Duplicate &&
                     (heldValueChangedDuringLoss || ShouldLandOnFirstHeldValueDuringPause()))
            {
                _input.MarkHeldReapplied();
                if (_signalLoss.IsPauseOwned)
                {
                    ReapplyHeldValueOnPause();
                    _input.MarkLastApplied(_input.LastHeldEffectiveSeconds);
                    _lastContinueFrame = null;
                    return;
                }
                applyOnce = true;
                applyReason = "held value change";
            }
            // v0.5.4 B6b（規則 4）: ランスルーのマスター停止の入口で、停止した値へ 1 回だけ合わせる
            // （規則 3 と同じ判定: |e| > tol なら relocate、以内なら何もしない）。以後は合わせた位置から
            // 1.0 で走る（varispeed は B4 が止め、この保持では relocate しない）。
            else if (processed.Diagnostic.Status == TimecodeFrameDiagnosticStatus.Duplicate &&
                     holdEntry && _effects.GetContext().SignalLossMode == LtcSignalLossMode.RunThrough)
            {
                AlignOnRunThroughHoldEntry();
                _input.MarkHeldReapplied();
                _input.MarkLastApplied(_input.LastHeldEffectiveSeconds);
                _lastContinueFrame = null;
                return;
            }
            // D20-b: 保持（Duplicate）でも、保持値が最後に適用した値から tolerance 超
            // ずれているときだけ 1 回適用する（定常の Duplicate ゲートは維持）。
            else if (processed.Diagnostic.Status == TimecodeFrameDiagnosticStatus.Duplicate &&
                     IsHeldValueFarFromLastApplied(rawSeconds, frameEndTimestamp))
            {
                _input.MarkHeldReapplied();
                applyOnce = true;
                applyReason = "held value change";
            }
            else
            {
                TryReapplyAfterFileLoadRelease();
                return;
            }
        }
        else
        {
            // D27-d: 値が進むフレームが来たら保持は明けたので、着地目標の保持値を捨てる。
            _input.OnNormalFrame();
            applyOnce = false;
            applyReason = "";
        }
        // T7: フレーム文脈は必ずこのフレームの処理の先頭で捨てる。抑止などで
        // ApplySync が走らないフレームに前のフレームの素材位置・再生位置を持ち越さない。
        _lastContinueFrame = null;
        // T3: 同期に使う値だけを入口で 1 回オフセットする。表示用の LastLtcSeconds は
        // 受信した LTC の生値を保つ。ここで作った effective 値を共有することで、
        // 同期判断・シーク・クリップ切替・ギャップ出入りが同じ量だけずれる。
        // T2: サンプル時計が有効なら、ここでフレーム終端からの経過（age）を足す。
        double effectiveSeconds = EffectiveSeconds(rawSeconds, frameEndTimestamp, applyOnce ? "jump" : "frame");
        _input.AcceptFrame(effectiveSeconds, rawSeconds, frameEndTimestamp);
        if (applyOnce)
        {
            // 通常時は診断 Jump・保持値の変更を信号回復の有効フレームに数えない
            // （ObserveValidFrame を呼ばない）。保持損失からの復帰は上の D27-b の経路。
            Log.Information("Timecode sync: applying the {Reason} frame once ltc={Ltc:F3}", applyReason, rawSeconds);
            RequestSyncEffective(effectiveSeconds);
            ApplyCorrection(effectiveSeconds);
            return;
        }

        ObserveValidFrame(rawSeconds, frameEndTimestamp, receivedAtMilliseconds);
        ApplyCorrection(effectiveSeconds);
    }

    /// <summary>
    /// D30: 次の 1 フレームの連続で確認できた Jump を 1 回適用する。保持損失中なら確認済みの
    /// Jump として復帰させ、着地は確認フレームの値で行う。
    /// </summary>
    private void ApplyConfirmedJump(
        TimecodeFrameDiagnosticStatus status, double rawSeconds, long frameEndTimestamp, long receivedAtMilliseconds,
        int heldRun)
    {
        // U8: 確認済みの Jump の適用を先に記録する（確認フレームが保持なら、その保持が Jump 後の 1 枚目）。
        _signalLoss.ObserveAppliedJump(receivedAtMilliseconds, SignalContext());

        if (status == TimecodeFrameDiagnosticStatus.Duplicate)
        {
            _signalLoss.ObserveHeldFrame(receivedAtMilliseconds, SignalContext(), heldRun);
            _input.MarkHeldEffective(SyncOffsetPolicy.Apply(rawSeconds,
                _effects.GetSyncOffsetMilliseconds?.Invoke() ?? SyncOffsetPolicy.DefaultMilliseconds));
        }
        else
        {
            _input.ClearHeldEffective();
        }

        if (_signalLoss.IsLost)
        {
            ApplySignalLossAction(_signalLoss.ObserveJumpFrame(receivedAtMilliseconds, SignalContext()));
        }

        _input.ClearHeldReapplied();
        // D31-b: 確認済みの適用で損失が明けた（または新しい値へ動いた）ので、損失中の着地値は捨てる。
        _input.ClearHeldLossLanding();
        _lastContinueFrame = null;
        double effectiveSeconds = EffectiveSeconds(rawSeconds, frameEndTimestamp, "jump");
        _input.AcceptFrame(effectiveSeconds, rawSeconds, frameEndTimestamp);
        Log.Information("Timecode sync: applying the confirmed Jump frame once ltc={Ltc:F3}", rawSeconds);
        RequestSyncEffective(effectiveSeconds);
        ApplyCorrection(effectiveSeconds);
    }

    /// <summary>
    /// v0.5.4（規則 4 の入口の数え方）: 入口の保持の 2 枚に数えるフレームか。Duplicate で、Fixed fps モードで
    /// デコーダ推定 fps が解決 fps と食い違わない（音が化けた最中の 1 枚を保持の証拠にしない）。
    /// D27-d の保持値の記録はこの判定に依らない。
    /// </summary>
    private static bool IsCountedHeldFrame(
        LtcFrameProcessingResult processed, LtcFrameReceivedEventArgs? sourceFrame, TimecodeFpsMode mode) =>
        processed.Diagnostic.Status == TimecodeFrameDiagnosticStatus.Duplicate &&
        !(sourceFrame != null &&
          JumpConfirmationPolicy.IsDetectedFpsSuspect(mode, sourceFrame.Fps, processed.ResolvedFps));

    /// <summary>
    /// D30 / v0.5.4 B7: 未確認の Jump を保留する理由（ログ用。判定はどれも同じで、次の 1 フレームの値の連続性）。
    /// Fixed fps モードでデコーダ推定 fps が食い違うときは "detected-fps"（層 1 の診断）、ほかは "value-continuity"。
    /// LTC には誤り検出が無く、化けた 1 枚（検証機の 2 時間試験で 1 回）をそのまま採ると +2.3 秒シークして
    /// 0.8 秒後に戻していた。本物の Jump は次のフレームが続くので、遅れは 1 フレーム（30fps で 33ms）。
    /// </summary>
    private string UnconfirmedJumpReason(LtcFrameProcessingResult processed, LtcFrameReceivedEventArgs? sourceFrame)
    {
        LtcSyncContext state = _effects.GetContext();
        if (sourceFrame != null &&
            JumpConfirmationPolicy.IsDetectedFpsSuspect(state.FpsMode, sourceFrame.Fps, processed.ResolvedFps))
            return "detected-fps";
        return "value-continuity";
    }

    /// <summary>
    /// D20-b: 保持（Duplicate）中の値が、最後に同期へ適用した値から一致許容を超えてずれているか。
    /// ずれていれば 1 回だけ適用する（ラッチは一致するフレームで解除）。
    /// </summary>
    private bool IsHeldValueFarFromLastApplied(double rawSeconds, long frameEndTimestamp)
    {
        if (_input.HeldReapplyDone || _input.LastAppliedLtcSeconds is not double applied)
            return false;
        if (!double.IsFinite(rawSeconds))
            return false;

        LtcSyncContext state = _effects.GetContext();
        double toleranceSeconds = SyncDecisionEngine.ToleranceSeconds(state.VideoFps, LastTimecodeFps);
        double effectiveSeconds = EffectiveSeconds(rawSeconds, frameEndTimestamp, "held");
        return Math.Abs(effectiveSeconds - applied) > toleranceSeconds;
    }

    /// <summary>
    /// D31-b: 保持損失中に、今回の保持値が「着地を発行済みの保持値」または「直前の保持値」から
    /// 変わったか。保持値は凍結値なので、半フレームを超える差を変化として扱う（同値の連続では
    /// false。着地を繰り返さない）。
    /// </summary>
    private bool IsHeldValueChangedDuringLoss(double heldEffectiveSeconds)
    {
        if (!_signalLoss.IsLost)
            return false;
        double? baseline = _input.HeldLossLandingSeconds ?? _input.LastHeldEffectiveSeconds;
        if (baseline is not double previous || !double.IsFinite(heldEffectiveSeconds))
            return false;

        double frameSeconds = LastTimecodeFps > 0 ? 1.0 / LastTimecodeFps : 0.04;
        return Math.Abs(heldEffectiveSeconds - previous) > frameSeconds * 0.5;
    }

    /// <summary>
    /// D35: 無音損失（SignalLoss）で一時停止した後に、保持値（Duplicate）が初めて届いたか。
    /// この損失でまだ着地しておらず、今回のフレームで保持値が分かったときに停止モードの
    /// 明示着地を行う（損失理由や値の到着順に依存しない）。
    /// v0.5.4（規則 4 の読み込みの入口）: 損失のままの読み込みの後もこの経路で着地する。規則 3 と同じく、
    /// 着地を待っている間（読み込み・シークの着地の前）は判定しない。
    /// </summary>
    private bool ShouldLandOnFirstHeldValueDuringPause() =>
        _signalLoss.IsPauseOwned && _input.HeldLossLandingSeconds is null &&
        _input.LastHeldEffectiveSeconds is not null && !_syncService.IsWaitingForLanding;

    /// <summary>
    /// D20-b (i): 保持 LTC（Duplicate）では通常の同期経路が走らないため、ロード解除だけを
    /// ここで観測し、解除されたら最後に受理したタイムコードを 1 回だけ適用する。
    /// D27-b: 解除が Tick 側の保留シーク再送（同期コーディネーターの完了）に先を越されても、
    /// 未回収の解除を回収して 1 回は適用する。着地先（clamp 位置）が決まらないうちは
    /// 解除を消費しない。
    /// </summary>
    private void TryReapplyAfterFileLoadRelease()
    {
        if (!_syncService.IsLoadingFile && !_syncService.HasPendingFileLoadRelease)
            return;
        if (_input.Accepted is not { } accepted)
            return;
        if (_effects.GetPlaybackSeconds == null || _effects.GetTotalRenderedFrames == null)
            return;
        if (_effects.GetPlaybackSeconds() is not double playback || !double.IsFinite(playback))
            return;

        LtcSyncContext state = _effects.GetContext();
        if (!SyncRules.CanReapplyAfterFileLoadRelease(
                state.IsPlayerReady, state.IsMonitoring, state.SyncEnabled, state.IsSeeking))
            return;
        // Single は尺が使えるまで待つ（保持値の clamp 着地先が決まらないため）。
        if (state.Mode != SyncMode.Continue && !SeekBarUpdateState.IsUsableDuration(state.DurationSeconds))
            return;

        if (!_syncService.PollFileLoadRelease(playback, _effects.GetTotalRenderedFrames()))
            return;

        Log.Information(
            "Timecode sync: reapplying the last accepted timecode once after file load ltc={Ltc:F3}",
            accepted.EffectiveSeconds);
        _input.MarkLastApplied(accepted.EffectiveSeconds);
        RequestSyncEffective(accepted.EffectiveSeconds);
    }

    /// <summary>
    /// T5: 粗いデッドゾーンの内側で残差を詰める。Smooth はシークを発行しない。
    /// shim がレート変更を拒否したら Smooth 使用不可として表示し、自動では Jump へ落とさない。
    /// </summary>
    private void ApplyCorrection(double ltcSeconds)
    {
        if (_effects.GetCorrectionMode == null ||
            _effects.ApplyRateInstant == null || _effects.SeekTo == null)
            return;

        LtcSyncContext state = _effects.GetContext();
        // D37-b: シーク中・着地未確認の位置では補正を評価しない。
        // v0.5.4 U4: A（着地の状態）の 1 つの条件（着地を待っている間）で止める（門 10）。
        if (!SyncRules.CanEvaluateCorrection(
                state.SyncEnabled, state.IsMonitoring, state.IsPlaybackPaused, state.IsSeeking,
                _syncService.IsWaitingForLanding))
            return;

        // v0.5.4 B4（chase モデルの規則 4）: 信号断・保持の持ち主（D の集合）がいる間、または
        // 直近のフレームが保持（Duplicate）の間は、速度補正を評価しない（rate は 1.0 のまま）。
        // 停止モードは一時停止が、ランスルーはここが保持中の補正を止める。ランスルーの保持に
        // 入るときは、直前に掛かっていた倍率を 1.0 に戻す。
        if (IsCorrectionHeldOff())
        {
            RestoreRateForHold();
            return;
        }

        if (_rate.RateRestorePending)
        {
            // T7: 一時停止中などで戻せなかった倍率を、評価の前に 1.0 へ戻す。
            if (!_effects.ApplyRateInstant(1.0))
                return;
            _rate.MarkRestored();
        }

        // 0.4.8: 再生位置が後退した直後は、位置を補正の入力として信用しない。復号が一時的に
        // 追いつかないとパイプライン位置が 2 系列を行き来し、残差が 1 標本ごとに 100ms 以上跳ねる。
        // その値で速度を上げ下げすると 0.9 倍と 1.1 倍の往復が続き、速度を上げるほど復号の負担も増える。
        // 不安定な間は倍率を 1.0 に戻して待ち、補正の系列（ゲート・中央値）も切る。
        if (_effects.IsPlaybackPositionUnstable?.Invoke() == true)
        {
            if (_rate.EnterPositionPause())
            {
                Log.Information(
                    "Smooth correction paused: playback position went backward (unstable); rate held at 1.0 rate={Rate:F5}",
                    _rate.LastAppliedRate);
            }
            ResetCorrection();
            return;
        }
        if (_rate.ExitPositionPause())
        {
            Log.Information("Smooth correction resumed: playback position is stable again");
        }

        // v0.5.4 B4b（chase モデルの規則 2）: 補正の誤差 e は、照会した再生位置ではなく、
        // 配信したフレームの PTS（着地の判定と同じ SyncPositionRead のサンプル）で測る。
        double residualSeconds;
        double targetSeconds;
        if (state.Mode == SyncMode.Continue)
        {
            // T7: Continue の素材位置はコーディネーターが 1 か所で出した値を使う（写像は変えない）。
            // B4b: 再生位置の側だけを配信したフレームの PTS に置き換える。
            if (_lastContinueFrame is not { CorrectionAllowed: true } frame)
                return;
            if (!TryReadDeliveredSeconds(out double delivered))
                return;
            residualSeconds = frame.MediaPositionSeconds - delivered;
            targetSeconds = frame.MediaPositionSeconds;
        }
        else
        {
            if (!TryReadDeliveredSeconds(out double delivered))
                return;
            // D33: 範囲外の LTC は補正しない（Jump の生値シーク・Smooth の暴走で MediaOut を
            // 越えない）。粗い判定の終端シークと Single の終端ホールドに任せる。範囲内は clamp は no-op。
            (double clipIn, double clipOut) = SyncDecisionEngine.ClipRange(
                state.MediaInSeconds, state.MediaOutSeconds, state.DurationSeconds);
            if (ltcSeconds < clipIn || ltcSeconds > clipOut)
                return;
            residualSeconds = ltcSeconds - delivered;
            targetSeconds = SyncDecisionEngine.ClampToClip(
                ltcSeconds, state.MediaInSeconds, state.MediaOutSeconds, state.DurationSeconds);
        }

        // v0.5.4 B6b-16/23（規則 3）: relocate・読み込みの着地を観測した直後の 1 サンプルは
        // varispeed しない（消費する）。シークの可否は粗い判定（15 の閾値と 13 のゲート）が決める。
        if (_syncService.ConsumeFirstSampleAfterLanding())
        {
            // 計数用（平常時の発火回数を数える。門ではない）。
            Log.Debug("sync.gate first-sample-after-landing mode={Mode} residualMs={ResidualMs:F1}",
                _effects.GetCorrectionMode(), residualSeconds * 1000.0);
            return;
        }

        // D37-c: 粗い判定と同じ前処理を補正の残差にも通す。ありえない変化の標本は捨て、
        // 採用した残差は直近窓の中央値にする（Smooth の制御則・Jump のしきい値は変えない）。
        double rawResidualSeconds = residualSeconds;
        double correctionToleranceSeconds =
            SyncDecisionEngine.ToleranceSeconds(state.VideoFps, LastTimecodeFps);
        double correctionGranularitySeconds = LastTimecodeFps > 0 ? 1.0 / LastTimecodeFps : 0.04;
        SeekDecisionGate.Result correctionGate = _rate.ObserveResidual(
            residualSeconds, correctionToleranceSeconds,
            _getQpc() / (double)Stopwatch.Frequency, correctionGranularitySeconds);
        if (correctionGate.Rejected)
        {
            LogCorrectionRejectedSample(correctionGate);
            return;
        }
        _rate.ClearRejectedLogged();
        residualSeconds = correctionGate.MedianSeconds;

        // v0.5.4 B4b: Smooth の不感帯は 1 映像フレーム（fps が不明なら LTC の 1 フレーム）。
        SyncCorrectionDecision decision = _correction.Evaluate(
            residualSeconds, targetSeconds, _effects.GetCorrectionMode(), _rate.SmoothAvailable, _getUtcNow(),
            SyncCorrectionController.FrameDurationSeconds(state.VideoFps, LastTimecodeFps));

        switch (decision.Action)
        {
            case SyncCorrectionActionType.SetRate:
                if (!_effects.ApplyRateInstant(decision.Rate))
                {
                    _rate.MarkSmoothUnavailable();
                    Log.Warning("Smooth 補正を使用できません（レート変更が拒否されました）。Jump への切替を検討してください");
                }
                else if (Math.Abs(decision.Rate - _rate.LastAppliedRate) >= 0.0005)
                {
                    _rate.MarkRateApplied(decision.Rate);
                    Log.Information(
                        "Smooth correction rate={Rate:F5} residualMs={ResidualMs:F1} rawResidualMs={RawResidualMs:F1} rejectedTotal={RejectedTotal}",
                        decision.Rate, residualSeconds * 1000.0, rawResidualSeconds * 1000.0,
                        correctionGate.RejectedTotal);
                    // v0.5.4 段 B2 の計測: 着地から 500ms 以内の速度補正（旧 門 9 が隠していた量）。
                    _syncService.NotePostLandingRateApplied(decision.Rate);
                }
                break;
            case SyncCorrectionActionType.Seek:
                // Smooth の倍率を Jump へ持ち込まない（shim 側では強制しない）。
                _effects.ApplyRateInstant(1.0);
                _rate.MarkRateApplied(1.0);
                if (_effects.SeekTo(decision.TargetSeconds))
                {
                    Log.Information(
                        "Jump correction seek target={Target:F3} residualMs={ResidualMs:F1} rawResidualMs={RawResidualMs:F1} rejectedTotal={RejectedTotal}",
                        decision.TargetSeconds, residualSeconds * 1000.0, rawResidualSeconds * 1000.0,
                        correctionGate.RejectedTotal);
                    _syncService.ReportSeekSent(decision.TargetSeconds, "jump-correction");
                }
                break;
        }

        string status =
            !_rate.SmoothAvailable || _correction.SmoothUnavailable ? "Smooth 使用不可: Jump に切替"
            : _correction.SmoothDisabled ? "Smooth 補正なし（効かない）"
            : "";
        _effects.SetCorrectionStatus?.Invoke(status);
    }

    /// <summary>
    /// v0.5.4 B4b（chase モデルの規則 2）: 補正の誤差を測る「配信したフレームの PTS」を読む。
    /// 着地の判定と同じ <see cref="LtcSyncEffects.ReadPosition"/> のサンプルを使う。配信の
    /// サンプルが無い間（配信世代が 0。`_ex` が無い旧 DLL のフォールバックも 0）は false を返し、
    /// 呼び出し側は補正を評価しない。
    /// </summary>
    private bool TryReadDeliveredSeconds(out double deliveredSeconds)
    {
        deliveredSeconds = 0.0;
        if (_effects.ReadPosition?.Invoke() is not { Succeeded: true, Sample: { } sample })
            return false;
        if (sample.DeliveredGeneration == 0)
            return false;
        deliveredSeconds = sample.DeliveredSeconds;
        return true;
    }

    /// <summary>
    /// D37-c: 物理的にありえない変化の標本を速度補正の入力から弾いたことを、乱れの切れ目に
    /// 1 回だけ残す（粗い判定の SeekDecisionGate と同じ形。除外回数を数えられる）。
    /// </summary>
    private void LogCorrectionRejectedSample(SeekDecisionGate.Result gate)
    {
        if (_rate.RejectedLogged)
            return;
        _rate.MarkRejectedLogged();
        Log.Information(
            "Correction gate: rejected unstable sample residualMs={ResidualMs:F1} previousMs={PreviousMs:F1} changeMs={ChangeMs:F1} allowedMs={AllowedMs:F1} dtMs={DtMs:F1} rejectedTotal={RejectedTotal}",
            gate.DeltaSeconds * 1000.0, gate.PreviousDeltaSeconds * 1000.0, gate.ChangeSeconds * 1000.0,
            gate.AllowedChangeSeconds * 1000.0, gate.DtSeconds * 1000.0, gate.RejectedTotal);
    }

    private static void LogFrameDiagnostics(
        LtcFrameReceivedEventArgs frame, LtcFrameProcessingResult processed, TimecodeFpsMode mode)
    {
        if (processed.ShouldLogFps)
            Log.Information(
                "LTC fps resolved mode={Mode} detectedFps={DetectedFps:F3} dropFrame={DropFrame} resolvedFps={ResolvedFps:F3}",
                mode, frame.Fps, frame.Timecode.DropFrame, processed.ResolvedFps);
        if (processed.Diagnostic.Status is not (TimecodeFrameDiagnosticStatus.Initial or TimecodeFrameDiagnosticStatus.Normal))
            Log.Warning(
                "LTC frame diagnostic status={Status} tc={Timecode} rawSeconds={RawSeconds:F3} resolvedSeconds={ResolvedSeconds:F3} deltaSeconds={DeltaSeconds:F3} deltaFrames={DeltaFrames:F2} detectedFps={DetectedFps:F3} resolvedFps={ResolvedFps:F3} mode={Mode}",
                processed.Diagnostic.Status, frame.Timecode, frame.RealTimeSeconds, processed.ResolvedSeconds,
                processed.Diagnostic.DeltaSeconds, processed.Diagnostic.DeltaFrames, frame.Fps, processed.ResolvedFps, mode);
        if (!processed.ShouldApplySync)
            Log.Information(
                "Timecode sync skipped due to LTC frame diagnostic status={Status} tc={Timecode} resolvedSeconds={ResolvedSeconds:F3} deltaSeconds={DeltaSeconds:F3} deltaFrames={DeltaFrames:F2}",
                processed.Diagnostic.Status, frame.Timecode, processed.ResolvedSeconds,
                processed.Diagnostic.DeltaSeconds, processed.Diagnostic.DeltaFrames);
    }

    private void ApplyFrame(LtcFrameProcessingResult processed)
    {
        _effects.ApplyFrameText(processed.TimecodeText, processed.RealTimeText);
        LastLtcSeconds = processed.ResolvedSeconds;
        _formatText = processed.FormatText;
        RefreshDisplay();
    }

    private void ObserveValidFrame(double rawSeconds, long frameEndTimestamp, long receivedAtMilliseconds)
    {
        ApplySignalLossAction(_signalLoss.ObserveValidFrame(receivedAtMilliseconds, SignalContext()));
        RefreshDisplay();
        _input.DiscardPendingSync();
        if (!_signalLoss.ShouldSuppressSync)
            RequestSync(rawSeconds, frameEndTimestamp);
    }

    public void Tick(long nowMilliseconds)
    {
        // v0.5.4 段 B1: UI タイマーでも着地の状態（新しい判定）を観測する（LTC のフレームの経路と独立）。
        ObserveLandingStateFromEffects();
        ApplySignalLossAction(_signalLoss.Evaluate(nowMilliseconds, SignalContext()));
        RefreshDisplay();
        if (_input.Pending is { } pending)
        {
            // T2: サンプル時計が有効なら、保留値は生値とフレーム終端を持ち、
            // 使う時点の age で実効値を取り直す（off は従来どおり実効値を再送する）。
            if (_sampleClockEnabled && pending.FrameEndTimestamp > 0)
                RequestSync(pending.RawSeconds, pending.FrameEndTimestamp, "tick");
            else
                RequestSyncEffective(pending.EffectiveSeconds);
        }
    }

    private LtcSignalLossContext SignalContext()
    {
        LtcSyncContext state = _effects.GetContext();
        return new(state.SignalLossMode, state.SyncEnabled,
            _monitoring.IsDetectionActive(state.IsMonitoring), !_gap.IsInactive, state.IsPlaybackPaused);
    }

    private void RefreshDisplay()
    {
        LtcDisplayState display = LtcDisplayStateFormatter.Format(
            _monitoring.IsDetectionActive(_effects.GetContext().IsMonitoring), _signalLoss.IsLost, _formatText);
        _effects.ApplyDisplay(display, LtcSignalLossPauseReasonFormatter.Format(_signalLoss.IsPauseOwned, _signalLoss.Reason));
    }

    /// <summary>
    /// D38 (a) / v0.5.4 段 B: 同期を適用しないフレーム（保持の Duplicate）でも、位置サンプルから
    /// 着地の状態（A）を観測する。着地していれば次の Jump が着地待ちとタイムアウトを通らない。
    /// </summary>
    private void ObservePendingSeekLanding() => ObserveLandingStateFromEffects();

    /// <summary>
    /// v0.5.4 段 B1: 位置を照会して着地の状態（新しい判定）を観測する。LTC のフレームの経路とは
    /// 独立に、UI タイマー（<see cref="Tick"/>）と保持の Duplicate から呼ぶ。B1 では判定に使わない。
    /// </summary>
    private void ObserveLandingStateFromEffects()
    {
        LtcSyncContext state = _effects.GetContext();
        if (!state.SyncEnabled || !state.IsMonitoring)
            return;
        if (_effects.ReadPosition?.Invoke() is { Succeeded: true } read)
            _syncService.ObserveLandingState(read, state.VideoFps, LastTimecodeFps);
    }

    private void ApplySignalLossAction(LtcSignalLossAction action, bool landOnHeldValue = true)
    {
        if (action == LtcSignalLossAction.None || !_effects.GetContext().IsPlayerReady)
            return;
        bool pause = action == LtcSignalLossAction.Pause;
        if (pause)
        {
            // D35: 停止モードの保持で止める直前に Smooth の残り倍率を 1.0 へ戻す
            // （一時停止後はレート変更を受け付けない）。
            RestoreRateBeforePolicyPause();
            _effects.SetSignalLossPaused(true);
            LtcSyncContext state = _effects.GetContext();
            Log.Information(
                "LTC signal lost: playback paused timeoutMs={TimeoutMs} reason={Reason}",
                state.SignalLossTimeoutMilliseconds, _signalLoss.Reason);
            // D35: 停止モードの保持は、損失理由（SignalLoss / TimecodeHeld）や保持値が損失宣言の
            // 前後どちらで分かったかに依らず、値が分かった時点で保持値へ明示的に 1 回着地する。
            // まだ値が無い（無音損失）ときは、その後の Duplicate が届いた時点で受信経路が着地する。
            // 読み込みの入口（landOnHeldValue = false）は、読み込みの着地の後に受信経路が着地する。
            if (landOnHeldValue &&
                (_input.LastHeldEffectiveSeconds is not null ||
                 _signalLoss.Reason == LtcSignalLossReason.TimecodeHeld))
                ReapplyHeldValueOnPause();
            return;
        }

        // v0.5.4 K5（§6 の 15）: 利用者を含むほかの持ち主が止めている間は、信号断の復帰でも再開しない
        // （境界ホールドの解除・ギャップの解除と同じ判定）。
        PauseOwners otherOwners = _effects.GetOtherPauseOwners?.Invoke() ?? PauseOwners.None;
        if (!SyncRules.ShouldResumeOnPolicyPauseRelease(otherOwners))
        {
            Log.Information("LTC signal restored: playback stays paused owners={Owners}", otherOwners);
            return;
        }

        _effects.SetSignalLossPaused(false);
        LtcSyncContext resumed = _effects.GetContext();
        Log.Information("LTC signal restored: playback resumed resumeFrames={ResumeFrames}", resumed.SignalResumeFrames);
    }

    /// <summary>
    /// D35: 保持損失で一時停止する直前に、Smooth 補正が残した倍率を 1.0 へ戻す。
    /// 一時停止後は rate.instant を受け付けないため、pause の前に戻す。
    /// </summary>
    private void RestoreRateBeforePolicyPause()
    {
        if (_effects.ApplyRateInstant == null)
            return;
        if (!_rate.RateRestorePending && !_rate.RateNotUnity)
            return;
        if (_effects.ApplyRateInstant(1.0))
        {
            _rate.MarkRestored();
            Log.Information("LTC signal lost: playback rate restored to 1.0 before pausing");
        }
        else
        {
            _rate.MarkRestorePending();
        }
    }

    /// <summary>
    /// v0.5.4 B4（chase モデルの規則 4）: 補正を保留すべき状態か。信号断・保持の持ち主
    /// （D の集合。ギャップ・境界ホールド・プロジェクト復元・利用者の一時停止を含む）がいる間と、
    /// 直近のフレームが保持（Duplicate。<see cref="LtcInputState.LastHeldEffectiveSeconds"/> が
    /// Normal で消えるまで残る）の間。
    /// </summary>
    private bool IsCorrectionHeldOff()
    {
        if (_input.LastHeldEffectiveSeconds is not null)
            return true;
        PauseOwners owners = _effects.GetOtherPauseOwners?.Invoke() ?? PauseOwners.None;
        return _signalLoss.IsPauseOwned || owners != PauseOwners.None;
    }

    /// <summary>
    /// v0.5.4 B4: 保持・信号断で補正を止めるときに、掛かったままの倍率を 1.0 に戻す
    /// （ランスルーは保持中も 1.0 で進む）。戻せなければ復帰待ちにする（既存の仕組みと同じ）。
    /// </summary>
    private void RestoreRateForHold()
    {
        if (_effects.ApplyRateInstant == null)
            return;
        if (!_rate.RateRestorePending && !_rate.RateNotUnity)
            return;
        if (_effects.ApplyRateInstant(1.0))
        {
            _rate.MarkRestored();
            Log.Information(
                "Timecode held or signal lost: rate restored to 1.0 (correction is not evaluated while held)");
        }
        else
        {
            _rate.MarkRestorePending();
        }
    }

    /// <summary>
    /// D27: 保持で一時停止したときの 1 回の着地。保持値へシークし、フレームが保持時刻に
    /// 対応した位置で止まるようにする。同期エンジンのデバウンス・保留状態には依存しない
    /// （停止時の 1 回だけ）。
    /// D27-d: 保持値は「保持として届いた最後の値（Duplicate）」を使う。保持直前の受理値は
    /// 1 フレーム手前で止まることがある（受領が 1 フレーム遅れる／Jump を適用しない場合）。
    /// D35: 同期の tolerance（0.240 秒）は経由せず明示的に着地する。許容内の行き過ぎ
    /// （0.0167〜0.240 秒）でも残さない。既に 1 フレーム以内なら省略する。
    /// </summary>
    private void ReapplyHeldValueOnPause()
    {
        double? held = _input.LastHeldEffectiveSeconds ?? _input.Accepted?.EffectiveSeconds;
        if (held is not double heldSeconds || _effects.SeekTo == null)
            return;
        LtcSyncContext state = _effects.GetContext();
        if (!TryGetHeldLandingTarget(heldSeconds, state, out double target))
            return;

        // D35: 1 フレーム以内なら既に保持位置なので省略する（停止中の微小残差でシークしない）。
        if (_effects.GetPlaybackSeconds?.Invoke() is double playback &&
            double.IsFinite(playback) &&
            Math.Abs(playback - target) <= HeldLandingFrameSeconds(state))
        {
            Log.Debug(
                "LTC timecode held: landing skipped (within one frame) position={Position:F3} target={Target:F3}",
                playback, target);
            return;
        }

        if (_effects.SeekTo(target))
        {
            _syncService.ReportSeekSent(target, "held-landing");
            Log.Information(
                "LTC timecode held: landing seek issued target={Target:F3} ltc={Ltc:F3}", target, heldSeconds);
        }
    }

    /// <summary>
    /// v0.5.4 B6b（規則 4）: ランスルーのマスター停止（保持）の入口で、停止した値へ 1 回だけ合わせる。
    /// 判定は規則 3 と同じ（|e| &gt; tol なら relocate、以内なら何もしない）。停止モードの
    /// <see cref="ReapplyHeldValueOnPause"/> と同じ着地先（写像・クリップの範囲・境界ホールド）を使い、
    /// この保持で合わせた値を保持着地の記録に残す（同じ保持では繰り返さない）。
    /// </summary>
    private void AlignOnRunThroughHoldEntry()
    {
        if (_input.LastHeldEffectiveSeconds is not double heldSeconds || _effects.SeekTo == null)
            return;
        // 規則 3: 着地を待っている間は判定しない（出ているシークが relocate）。保持着地の記録を
        // 残さないので、着地した後の次の保持フレームがこの入口の判定をする。
        if (_syncService.IsWaitingForLanding)
            return;
        LtcSyncContext state = _effects.GetContext();
        if (!TryGetHeldLandingTarget(heldSeconds, state, out double target))
            return;
        if (_effects.GetPlaybackSeconds?.Invoke() is not double playback || !double.IsFinite(playback))
            return;
        double toleranceSeconds = SyncDecisionEngine.ToleranceSeconds(state.VideoFps, LastTimecodeFps);
        if (Math.Abs(playback - target) <= toleranceSeconds)
        {
            Log.Debug(
                "LTC timecode held (run-through): entry alignment not needed position={Position:F3} target={Target:F3}",
                playback, target);
            return;
        }
        if (_effects.SeekTo(target))
        {
            _syncService.ReportSeekSent(target, "hold-entry");
            // v0.6.0 S-4: 目標がクリップの端へ収めた値なら、端へのシークを出した記録をコーディネーターに残す
            // （同期シークの NoteBoundarySeek と同じ形）。記録が無いと、境界の保持は位置が端の ±2 映像フレーム以内の
            // 瞬間を読めたときしか入らず、60fps で着地が即時だと窓を過ぎて出口の先へ走り続けた。
            if (state.Mode == SyncMode.Single && target != heldSeconds)
                _single().NoteHoldEntryBoundarySeek(target);
            Log.Information(
                "LTC timecode held (run-through): entry alignment seek issued target={Target:F3} ltc={Ltc:F3} position={Position:F3}",
                target, heldSeconds, playback);
        }
    }

    /// <summary>
    /// 保持値の着地先（Continue はタイムライン → 素材位置、Single はクリップの範囲）。境界ホールドが
    /// 一時停止の持ち主なら端で受け持つので false。着地先が決まったら、この損失（保持）で着地を試みた
    /// 保持値として記録する（D31-b）。
    /// v0.5.4（S-4 の回帰の修正）: 写像（尺・fps）が分かるまでは判定せず false（記録もしない）。
    /// 分かった後の最初の保持のフレームで、呼び出し側（規則 4 の入口・停止の着地）が 1 回だけ合わせる。
    /// </summary>
    private bool TryGetHeldLandingTarget(double heldSeconds, LtcSyncContext state, out double target)
    {
        target = 0.0;
        if (!state.IsMonitoring || !state.SyncEnabled || state.IsSeeking)
            return false;
        // 読み込みの直後は尺が 0（アプリは UI タイマーが取れたときに入れる）。この間にクリップの端へ収めると、
        // 端が素材の終わりちょうど（MediaOut）になり、そこへシークして EOF に入る（S-4）。同期の判定（エンジン）の
        // bad-duration と同じく、尺が使えない間は判定しない。
        if (!SeekBarUpdateState.IsUsableDuration(state.DurationSeconds))
        {
            Log.Debug("LTC timecode held: landing deferred until the duration is known ltc={Ltc:F3}", heldSeconds);
            return false;
        }

        if (state.Mode == SyncMode.Continue)
        {
            TimelineQueryResult result = _playlist.FindTrackAtTimelinePosition(heldSeconds);
            if (result.Status != TimelineQueryStatus.OnTrack)
                return false;
            target = result.MediaPositionSeconds;
        }
        else
        {
            // D35-b: D33 の境界ホールド中は端で受け持つ。端への明示着地は保留シークを作り、
            // 解除時の範囲内 LTC への着地を抑止するため発行しない。
            // v0.5.4 U5: 判定は一時停止の持ち主の集合（D）に畳む（境界ホールドが持ち主なら飛ばす）。
            PauseOwners owners = _effects.GetOtherPauseOwners?.Invoke() ?? PauseOwners.None;
            if (SyncRules.ShouldSkipHeldLanding(owners))
            {
                _input.MarkHeldLossLanding(heldSeconds);
                Log.Debug(
                    "LTC timecode held: landing skipped (boundary hold is a pause owner) ltc={Ltc:F3}",
                    heldSeconds);
                return false;
            }
            // 端（最後のコマの頭）は映像 fps から決まる。fps が分からない間も判定しない（端が素材の終わりちょうどになる）。
            if (!SyncDecisionEngine.IsUsableFps(state.VideoFps))
            {
                Log.Debug("LTC timecode held: landing deferred until the video fps is known ltc={Ltc:F3}", heldSeconds);
                return false;
            }
            // D29: 着地先はほかの経路と同じくクリップの [MediaIn, MediaOut ?? 尺] に収める。
            // 以前は尺だけで収めていたため、LTC が入口より手前で止まると、クリップの外（入口の手前）の
            // 絵へ着地した（検証機の S-2、クリップ [10,18] で LTC を 8.0 に止めた回）。
            target = SyncDecisionEngine.ClampToClip(
                heldSeconds, state.MediaInSeconds, state.MediaOutSeconds, state.DurationSeconds,
                state.VideoFps);
        }

        // D31-b: この損失で着地を試みた保持値を覚え、値が変わったときだけ再度着地する。
        _input.MarkHeldLossLanding(heldSeconds);
        return true;
    }

    /// <summary>D35: 着地の省略判定に使う 1 フレーム。映像 fps が無ければ LTC の 1 フレーム。</summary>
    private double HeldLandingFrameSeconds(LtcSyncContext state) =>
        SyncCorrectionController.FrameDurationSeconds(state.VideoFps, LastTimecodeFps);

    /// <summary>
    /// D35-b: D33 の境界ホールド（Single）が解除されたときに呼ぶ。ホールド中に残った端への
    /// 保留シークと保持着地のラッチを必ず解除し、解除後の範囲内 LTC への着地を抑止しない。
    /// v0.5.2 段 1 の追加: ほかの入口と同じくできごとを記録し、消す処理は OnLifecycle に置く。
    /// </summary>
    internal void NotifyClipBoundaryHoldReleased()
    {
        SyncLifecycle.Record(SyncLifecycleEvent.BoundaryHoldReleased, "left-boundary");
        OnLifecycle(SyncLifecycleEvent.BoundaryHoldReleased);
        Log.Information("Single mode: boundary hold released; pending seek state and held landing latch cleared");
    }

    private SyncRequestResult ApplySync(double seconds, bool gapDisplayOnly = false)
    {
        _lastContinueFrame = null;
        LtcSyncContext state = _effects.GetContext();
        if (!SyncRules.CanApplySync(
                state.IsPlayerReady, state.IsMonitoring, state.SyncEnabled,
                state.IsSeeking, _signalLoss.ShouldSuppressSync))
            return SyncRequestResult.Complete;
        if (state.Mode != SyncMode.Continue)
        {
            // U1: 古い再適用では Single の同期（シーク目標）も次の有効フレームに任せる。
            if (gapDisplayOnly)
                return SyncRequestResult.Complete;
            if (state.SyncEnabled && !state.IsSeeking && _playlist.Current != null)
                _effects.ResumeProjectRestorePause();
            return _single().Apply(seconds);
        }
        TimelineQueryResult result = _playlist.FindTrackAtTimelinePosition(seconds);
        string? trackName = result.Track?.Name;
        if (_queryLog.ShouldLog(result.Status, trackName, result.MediaPositionSeconds, DateTime.UtcNow))
            Log.Debug("Continue mode query result: status={Status} track={Track} mediaPos={MediaPos:F3}",
                result.Status, trackName ?? "null", result.MediaPositionSeconds);
        switch (result.Status)
        {
            case TimelineQueryStatus.OnTrack:
                // U1: 古い再適用では OnTrack の同期（シーク・トラック切替・pause 解除）も
                // 次の有効フレームに任せる。ギャップ表示の切替は下の分岐だけが行う。
                if (gapDisplayOnly)
                    return SyncRequestResult.Complete;
                _effects.ResumeProjectRestorePause();
                ContinueFrameContext frame = _continue().HandleFrame(result, seconds);
                _lastContinueFrame = frame;
                if (frame.SwitchedTrack)
                {
                    // T7: トラック切替（ロード成功）で補正状態を捨て、Smooth を再試行できるようにする。
                    ResetCorrection();
                    _rate.ResetSmoothAvailability();
                }
                if (frame.ExitedGap)
                    ResetCorrection();
                return frame.Request;
            case TimelineQueryStatus.Gap:
                // T7: ギャップ中は補正を評価しない（出入りのたびに状態を捨てる）。
                ResetCorrection();
                if (_gap.ShouldTransitionFromFreezeToBlack(state.GapBehavior))
                    _effects.ClearGapFreezeFrame();
                _effects.UpdateTimelinePosition(seconds);
                double? loadedPosition = _effects.GetPlaybackSeconds?.Invoke();
                GapEnterAction action = _gap.DecideGapEnter(result, state.GapBehavior,
                    state.LoadedTrackId, state.VideoFps, state.DurationSeconds, loadedPosition);
                GapEnterCoordinator coordinator = _gapCoordinator();
                new GapEnterActionDispatcher(new GapEnterActionHandlers(
                    coordinator.EnterBlackGap, coordinator.EnterForceBlack, null,
                    coordinator.StartGapFreezeCaptureForCurrentTrack,
                    coordinator.LoadPreviousTrackFinalFrameForGapFreeze,
                    coordinator.LoadNextTrackFirstFrameForGapFreeze,
                    coordinator.CaptureCurrentFrameForGapFreeze)).Execute(action, result);
                _effects.UpdateCurrentTrackLabel();
                break;
            case TimelineQueryStatus.NoTracks:
                ResetCorrection();
                if (_gap.ShouldTransitionFromFreezeToBlack(state.GapBehavior))
                    _effects.ClearGapFreezeFrame();
                _gapCoordinator().HandleNoTracks();
                break;
        }
        return SyncRequestResult.Complete;
    }

    private void ExitGapForManualControl()
    {
        LtcSyncContext state = _effects.GetContext();
        if (!GapStateExitPolicy.ShouldExit(state.SyncEnabled, state.Mode, !_gap.IsInactive))
            return;
        GapExitAction exit = _gap.DecideGapExit();
        _gap.ResetAll();
        // v0.5.4 K5（§6 の 15）: 信号断とほかの持ち主（利用者を含む）が止めていれば再開しない
        // （境界ホールドの解除・信号断の復帰と同じ判定。ResetAll の後に読むとギャップ自身は入らない）。
        PauseOwners otherOwners = _effects.GetOtherPauseOwners?.Invoke() ?? PauseOwners.None;
        if (_signalLoss.IsPauseOwned)
            otherOwners |= PauseOwners.SignalLoss;
        if (exit.ShouldResumePlayback && state.IsPlayerReady &&
            SyncRules.ShouldResumeOnPolicyPauseRelease(otherOwners))
        {
            _effects.ResumeGapPause();
        }
        _effects.ClearGapFreezeFrame();
        _effects.RefreshCurrentVideoFrame();
        Log.Information("Gap state cleared for manual control syncEnabled={SyncEnabled} mode={Mode}",
            state.SyncEnabled, state.Mode);
        _effects.UpdateCurrentTrackLabel();
    }

    /// <summary>
    /// v0.5.2 段 0: ラッチ（一時状態）が立っているかの読み取り専用の写し（特性テスト用）。
    /// キーはラッチの意味の名前で、内部の持ち方が変わっても同じ意味で返す。状態は変えない。
    /// </summary>
    internal IReadOnlyDictionary<string, bool> LatchSnapshot() => new Dictionary<string, bool>
    {
        ["heldReapplyDone"] = _input.HeldReapplyDone,
        ["pendingJump"] = _input.PendingJumpSeconds is not null,
        ["pendingSync"] = _input.Pending is not null,
        ["lastHeldEffective"] = _input.LastHeldEffectiveSeconds is not null,
        ["heldLossLanding"] = _input.HeldLossLandingSeconds is not null,
        ["lastAppliedLtc"] = _input.LastAppliedLtcSeconds is not null,
        ["lastAcceptedLtc"] = _input.Accepted is not null,
        ["rateRestorePending"] = _rate.RateRestorePending,
        ["smoothUnavailable"] = !_rate.SmoothAvailable,
        // 倍率が 1.0 でないまま残っているか（ResetCorrection と同じ判定幅）。
        ["rateNotUnity"] = _rate.RateNotUnity,
        ["correctionPausedForPosition"] = _rate.CorrectionPausedForPosition,
    };

    /// <summary>v0.5.2 段 0: 信号断のポリシーのラッチの写し（特性テスト用。状態は変えない）。</summary>
    internal IReadOnlyDictionary<string, bool> SignalLossLatchSnapshot() => _signalLoss.LatchSnapshot();
}
