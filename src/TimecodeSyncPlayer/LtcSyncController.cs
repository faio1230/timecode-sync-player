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
    int SignalResumeFrames);

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
    Func<double>? GetSyncOffsetMilliseconds = null);

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
    private bool _smoothAvailable = true;
    private double _lastAppliedRate = 1.0;
    private bool _rateRestorePending;
    private double? _lastAcceptedLtcSeconds;
    private double _lastAcceptedRawSeconds;
    private long _lastAcceptedFrameEndTimestamp;
    // D27-d: 保持（Duplicate）として届いた最後の値。停止時の着地目標は保持値そのものにし、
    // 保持直前の受理値（1 フレーム手前になり得る）を使わない。Normal/Initial で解除する。
    private double? _lastHeldEffectiveSeconds;
    // D20-b: 同期へ実際に適用した最後の値（保持値の変更判定に使う）。
    private double? _lastAppliedLtcSeconds;
    // D20-b (i): 同一の Jump 連続で何度も適用しないためのラッチ（Normal/Initial で解除）。
    private bool _jumpAppliedOnce;
    // D20-b: 保持値の変更で 1 回だけ適用したことを示すラッチ（Normal/Initial で解除）。
    private bool _heldReapplyDone;
    // D30: 未確認の Jump。写像がギャップ／別トラック、または Fixed モードでデコーダ推定 fps が
    // 食い違う Jump を保持し、次の 1 フレームの連続（同値 Duplicate か +1 フレーム）で確認して
    // から適用する。誤デコード 1 枚でギャップ進入・トラック切替・保持復帰を起こさない。
    private double? _pendingJumpSeconds;
    private long _pendingJumpReceivedAt;
    private long _pendingJumpFrameEndTimestamp;
    private double? _pendingSyncSeconds;
    private double _pendingSyncRawSeconds;
    private long _pendingSyncFrameEndTimestamp;
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
    }

    public double LastLtcSeconds { get; private set; }
    public double LastTimecodeFps => _frames.LastTimecodeFps;

    /// <summary>
    /// 環境変数の解釈（T2 段 3: 既定 on）。明示的な off（大文字小文字不問）のときだけ無効。
    /// </summary>
    internal static bool IsSampleClockEnabled(string? value)
        => value is null || !value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase);

    /// <summary>テスト・診断用: 直近フレームの Continue 補正文脈（フレーム先頭で捨てる）。</summary>
    internal ContinueFrameContext? LastContinueFrame => _lastContinueFrame;

    public void SyncEnabledChanged()
    {
        ResetCorrection();
        _smoothAvailable = true;
        if (!_effects.GetContext().SyncEnabled)
            _syncService.ClearSeekState();
        ExitGapForManualControl();
        ReapplyLastAcceptedFrame();
    }

    public void SyncModeChanged()
    {
        ResetCorrection();
        _smoothAvailable = true;
        _frames.ResetDiagnostics();
        _pendingJumpSeconds = null;
        _pendingJumpFrameEndTimestamp = 0;
        _syncService.ClearSeekState();
        ExitGapForManualControl();
        _effects.UpdateCurrentTrackLabel();
        ReapplyLastAcceptedFrame();
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
        _pendingSyncSeconds = null;
        LtcSyncContext state = _effects.GetContext();
        if (_lastAcceptedLtcSeconds is null || !state.IsMonitoring ||
            !state.SyncEnabled || state.IsSeeking || _signalLoss.ShouldSuppressSync)
            return;

        bool stale = IsStaleReapply();
        if (_sampleClockEnabled && _lastAcceptedFrameEndTimestamp > 0)
            Log.Debug("LTC sample clock: reapply ageMs={AgeMs:F1} deferred={Deferred}",
                ReapplyAgeMilliseconds(), stale);

        if (stale)
        {
            // U1: 最後のフレーム終端から 0.5 秒より古い再適用では同期要求（シーク目標）を
            // 出さず、次の有効フレームに任せる。ギャップ表示の切替は ApplySync の
            // ギャップ分岐が即時に行う（gapDisplayOnly）。
            ApplySync(EffectiveSeconds(_lastAcceptedRawSeconds, _lastAcceptedFrameEndTimestamp, ReapplyAgeSource),
                gapDisplayOnly: true);
            return;
        }

        RequestSync(_lastAcceptedRawSeconds, _lastAcceptedFrameEndTimestamp, ReapplyAgeSource);
    }

    /// <summary>
    /// U1: 再適用時点で最後のフレーム終端が 0.5 秒より古い（停止前の値である）か。
    /// サンプル時計 off では age を使わないため常に false。
    /// </summary>
    private bool IsStaleReapply()
    {
        if (!_sampleClockEnabled || _lastAcceptedFrameEndTimestamp <= 0)
            return false;
        double ageSeconds = (_getQpc() - _lastAcceptedFrameEndTimestamp) / (double)Stopwatch.Frequency;
        return ageSeconds is < 0 or > MaxSampleClockAgeSeconds;
    }

    private double ReapplyAgeMilliseconds() =>
        _lastAcceptedFrameEndTimestamp <= 0
            ? 0.0
            : (_getQpc() - _lastAcceptedFrameEndTimestamp) * 1000.0 / Stopwatch.Frequency;

    public void CancelPendingSync()
    {
        _pendingSyncSeconds = null;
        _pendingJumpSeconds = null;
        _pendingJumpFrameEndTimestamp = 0;
        // T7: 手動シークは補正状態（Smooth の無効化を含む）も捨てる。
        ResetCorrection();
    }

    /// <summary>
    /// T9: 粗い同期シークの発行で、着地直後の Smooth 速度上限（±0.20）の窓を開く。
    /// Jump の補正シークも ReportSeekSent を通るため、Smooth のときだけ通知する
    /// （窓を参照するのは Smooth だけだが、無駄な状態更新を避ける）。
    /// </summary>
    private void OnSeekIssued()
    {
        if (_effects.GetCorrectionMode?.Invoke() != SyncCorrectionMode.Smooth)
            return;
        _correction.NotifyLanding(_getUtcNow());
    }

    /// <summary>T7: 操作者の再生・一時停止、プロジェクト差し替えで補正状態を捨てる。</summary>
    public void CorrectionReset() => ResetCorrection();

    /// <summary>
    /// T7: 補正状態を捨て、プレイヤーに掛けた倍率が残っていれば 1.0 に戻す。
    /// 一時停止中などで戻せないときは次の評価可能フレームの評価前に戻す。
    /// </summary>
    private void ResetCorrection()
    {
        _correction.Reset();
        if (_rateRestorePending || Math.Abs(_lastAppliedRate - 1.0) < 0.0005)
            return;
        if (_effects.ApplyRateInstant?.Invoke(1.0) == true)
            _lastAppliedRate = 1.0;
        else
            _rateRestorePending = true;
    }

    private void RequestSync(double rawSeconds, long frameEndTimestamp, string source = "frame")
    {
        _pendingSyncRawSeconds = rawSeconds;
        _pendingSyncFrameEndTimestamp = frameEndTimestamp;
        RequestSyncEffective(EffectiveSeconds(rawSeconds, frameEndTimestamp, source));
    }

    private void RequestSyncEffective(double effectiveSeconds)
    {
        // U1 計測: コンボ変更・フレーム受信からギャップ状態再評価までの所要。
        long started = Stopwatch.GetTimestamp();
        SyncRequestResult result = ApplySync(effectiveSeconds);
        _pendingSyncSeconds = result == SyncRequestResult.Deferred ? effectiveSeconds : null;
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
        _pendingJumpSeconds = null;
        _pendingJumpFrameEndTimestamp = 0;
        _frames.ResetForFpsMode(_effects.GetContext().FpsMode);
    }

    public void MonitoringChanged()
    {
        _lastAcceptedLtcSeconds = null;
        _lastAppliedLtcSeconds = null;
        _lastHeldEffectiveSeconds = null;
        _pendingSyncSeconds = null;
        _pendingJumpSeconds = null;
        _pendingJumpFrameEndTimestamp = 0;
        _jumpAppliedOnce = false;
        _heldReapplyDone = false;
        if (_effects.GetContext().IsMonitoring)
        {
            _monitoring.MarkStarted();
            _signalLoss.Reset();
            _formatText = "fps: 検出中...";
        }
        else if (!_monitoring.IsDetectionActive(isReportedRunning: false))
        {
            _signalLoss.Reset();
            _formatText = "LTC 停止中";
        }
        RefreshDisplay();
    }

    public void DeviceEnumerationFailed()
    {
        _formatText = "LTC デバイス列挙失敗";
        RefreshDisplay();
    }

    public void MonitorStopped(Exception? exception)
    {
        _lastAcceptedLtcSeconds = null;
        _lastAppliedLtcSeconds = null;
        _lastHeldEffectiveSeconds = null;
        _pendingSyncSeconds = null;
        _pendingJumpSeconds = null;
        _pendingJumpFrameEndTimestamp = 0;
        _jumpAppliedOnce = false;
        _heldReapplyDone = false;
        if (_monitoring.MarkStopped(exception))
        {
            _signalLoss.Reset();
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
        // D30: 未確認 Jump の確認。直後の 1 フレームが同値の Duplicate か +1 フレームなら、
        // その値を確認済み Jump として適用する（保持損失からの復帰も確認後に行う）。
        if (_pendingJumpSeconds is double pendingJump)
        {
            _pendingJumpSeconds = null;
            bool withinWindow = JumpConfirmationPolicy.IsWithinConfirmationWindow(
                _pendingJumpFrameEndTimestamp, frameEndTimestamp,
                _pendingJumpReceivedAt, receivedAtMilliseconds, LastTimecodeFps);
            if (withinWindow &&
                JumpConfirmationPolicy.IsConfirmedBy(
                    pendingJump, rawSeconds, LastTimecodeFps, processed.Diagnostic.Status))
            {
                ApplyConfirmedJump(processed.Diagnostic.Status, rawSeconds, frameEndTimestamp, receivedAtMilliseconds);
                return;
            }
            if (!withinWindow)
            {
                // D31: 窓はサンプル時計（FrameEndTimestamp）優先。壁時計（受信時刻）は参考値として出す。
                Log.Information(
                    "Timecode sync: dropping out-of-window pending Jump frame ltc={Ltc:F3} next={Next:F3} streamMs={StreamMs:F1} wallMs={WallMs}",
                    pendingJump, rawSeconds,
                    JumpConfirmationPolicy.SampleClockDifferenceMilliseconds(
                        _pendingJumpFrameEndTimestamp, frameEndTimestamp) ?? -1.0,
                    receivedAtMilliseconds - _pendingJumpReceivedAt);
            }
        }

        bool applyOnce;
        string applyReason;
        if (!processed.ShouldApplySync)
        {
            // D27: 解読は続いているが値が進まない保持（Duplicate）を信号停止の判定へ伝える。
            // 無音（フレームが届かない）と同じ経路で損失になり、損失の理由だけが分かれる。
            // D27-d: 停止時の着地目標に使う「保持として届いた値」もここで記録する
            // （保持直前の受理値は 1 フレーム手前になり得る）。
            if (processed.Diagnostic.Status == TimecodeFrameDiagnosticStatus.Duplicate)
            {
                _signalLoss.ObserveHeldFrame(receivedAtMilliseconds, SignalContext());
                // D27-d: 着地目標は保持として届いた値そのもの。保持値は凍結されて進まないため、
                // サンプル時計の age は足さず T3 オフセットだけ適用する。
                _lastHeldEffectiveSeconds = SyncOffsetPolicy.Apply(rawSeconds,
                    _effects.GetSyncOffsetMilliseconds?.Invoke() ?? SyncOffsetPolicy.DefaultMilliseconds);
            }
            // D30: 写像がギャップ／別トラックの Jump と、Fixed モードでデコーダ推定 fps が
            // 食い違う Jump は未確認にして次の 1 フレームの連続を待つ（誤値 1 枚で状態を動かさない）。
            if (processed.Diagnostic.Status == TimecodeFrameDiagnosticStatus.Jump)
            {
                string? deferReason = UnconfirmedJumpReason(processed, sourceFrame, rawSeconds, frameEndTimestamp);
                if (deferReason != null)
                {
                    _pendingJumpSeconds = rawSeconds;
                    _pendingJumpReceivedAt = receivedAtMilliseconds;
                    _pendingJumpFrameEndTimestamp = frameEndTimestamp;
                    Log.Information(
                        "Timecode sync: holding unconfirmed Jump frame ltc={Ltc:F3} reason={Reason}",
                        rawSeconds, deferReason);
                    return;
                }

                // D27-b: 保持が理由の損失中は、値が動いた Jump 1 枚で即復帰する（無音からの
                // 復帰は既存どおり有効フレーム N 枚）。復帰した Jump は新値へ 1 回だけ着地させる
                // （ラッチ済みの Jump でも数えるためラッチを解除してから適用する）。
                // D27-c: 保持フレームの途切れで理由が信号断へ下がっていても、保持の直後の Jump は
                // 復帰に数える（判定は ObserveJumpFrame 側。無音からの Jump は復帰しない）。
                if (_signalLoss.IsLost)
                {
                    ApplySignalLossAction(_signalLoss.ObserveJumpFrame(receivedAtMilliseconds, SignalContext()));
                    if (!_signalLoss.IsLost)
                        _jumpAppliedOnce = false;
                }
                // D20-b (i): Jump の直後は 1 回だけ新値で適用する。
                if (!_jumpAppliedOnce)
                {
                    _jumpAppliedOnce = true;
                    applyOnce = true;
                    applyReason = "first Jump";
                }
                else
                {
                    TryReapplyAfterFileLoadRelease();
                    return;
                }
            }
            // D20-b: 保持（Duplicate）でも、保持値が最後に適用した値から tolerance 超
            // ずれているときだけ 1 回適用する（定常の Duplicate ゲートは維持）。
            else if (processed.Diagnostic.Status == TimecodeFrameDiagnosticStatus.Duplicate &&
                     IsHeldValueFarFromLastApplied(rawSeconds, frameEndTimestamp))
            {
                _heldReapplyDone = true;
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
            _jumpAppliedOnce = false;
            _heldReapplyDone = false;
            // D27-d: 値が進むフレームが来たら保持は明けたので、着地目標の保持値を捨てる。
            _lastHeldEffectiveSeconds = null;
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
        _lastAcceptedLtcSeconds = effectiveSeconds;
        _lastAcceptedRawSeconds = rawSeconds;
        _lastAcceptedFrameEndTimestamp = frameEndTimestamp;
        _lastAppliedLtcSeconds = effectiveSeconds;
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
        TimecodeFrameDiagnosticStatus status, double rawSeconds, long frameEndTimestamp, long receivedAtMilliseconds)
    {
        if (status == TimecodeFrameDiagnosticStatus.Duplicate)
        {
            _signalLoss.ObserveHeldFrame(receivedAtMilliseconds, SignalContext());
            _lastHeldEffectiveSeconds = SyncOffsetPolicy.Apply(rawSeconds,
                _effects.GetSyncOffsetMilliseconds?.Invoke() ?? SyncOffsetPolicy.DefaultMilliseconds);
        }
        else
        {
            _lastHeldEffectiveSeconds = null;
        }

        if (_signalLoss.IsLost)
        {
            ApplySignalLossAction(_signalLoss.ObserveJumpFrame(receivedAtMilliseconds, SignalContext()));
            if (!_signalLoss.IsLost)
                _jumpAppliedOnce = false;
        }

        _jumpAppliedOnce = true;
        _heldReapplyDone = false;
        _lastContinueFrame = null;
        double effectiveSeconds = EffectiveSeconds(rawSeconds, frameEndTimestamp, "jump");
        _lastAcceptedLtcSeconds = effectiveSeconds;
        _lastAcceptedRawSeconds = rawSeconds;
        _lastAcceptedFrameEndTimestamp = frameEndTimestamp;
        _lastAppliedLtcSeconds = effectiveSeconds;
        Log.Information("Timecode sync: applying the confirmed Jump frame once ltc={Ltc:F3}", rawSeconds);
        RequestSyncEffective(effectiveSeconds);
        ApplyCorrection(effectiveSeconds);
    }

    /// <summary>
    /// D30: この Jump を即時適用できない理由（null なら即時）。ギャップ（先頭オフセットを含む）／
    /// 現在と別トラックへの写像と、Fixed fps モードでのデコーダ推定 fps の食い違いを未確認とする。
    /// Single はトラックの写像を持たないため、写像による保留はしない。
    /// </summary>
    private string? UnconfirmedJumpReason(
        LtcFrameProcessingResult processed, LtcFrameReceivedEventArgs? sourceFrame,
        double rawSeconds, long frameEndTimestamp)
    {
        LtcSyncContext state = _effects.GetContext();
        if (sourceFrame != null &&
            JumpConfirmationPolicy.IsDetectedFpsSuspect(state.FpsMode, sourceFrame.Fps, processed.ResolvedFps))
            return "detected-fps";
        if (state.Mode == SyncMode.Continue)
        {
            double effectiveSeconds = EffectiveSeconds(rawSeconds, frameEndTimestamp, "jump");
            TimelineQueryResult result = _playlist.FindTrackAtTimelinePosition(effectiveSeconds);
            if (result.Status != TimelineQueryStatus.OnTrack || result.Track?.Id != state.LoadedTrackId)
                return "track-or-gap";
        }
        return null;
    }

    /// <summary>
    /// D20-b: 保持（Duplicate）中の値が、最後に同期へ適用した値から一致許容を超えてずれているか。
    /// ずれていれば 1 回だけ適用する（ラッチは一致するフレームで解除）。
    /// </summary>
    private bool IsHeldValueFarFromLastApplied(double rawSeconds, long frameEndTimestamp)
    {
        if (_heldReapplyDone || _lastAppliedLtcSeconds is not double applied)
            return false;
        if (!double.IsFinite(rawSeconds))
            return false;

        LtcSyncContext state = _effects.GetContext();
        double toleranceSeconds = SyncDecisionEngine.ToleranceSeconds(state.VideoFps, LastTimecodeFps);
        double effectiveSeconds = EffectiveSeconds(rawSeconds, frameEndTimestamp, "held");
        return Math.Abs(effectiveSeconds - applied) > toleranceSeconds;
    }

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
        if (_lastAcceptedLtcSeconds is not double accepted)
            return;
        if (_effects.GetPlaybackSeconds == null || _effects.GetTotalRenderedFrames == null)
            return;
        if (_effects.GetPlaybackSeconds() is not double playback || !double.IsFinite(playback))
            return;

        LtcSyncContext state = _effects.GetContext();
        if (!state.IsPlayerReady || !state.IsMonitoring || !state.SyncEnabled || state.IsSeeking)
            return;
        // Single は尺が使えるまで待つ（保持値の clamp 着地先が決まらないため）。
        if (state.Mode != SyncMode.Continue && !SeekBarUpdateState.IsUsableDuration(state.DurationSeconds))
            return;

        if (!_syncService.PollFileLoadRelease(playback, _effects.GetTotalRenderedFrames()))
            return;

        Log.Information(
            "Timecode sync: reapplying the last accepted timecode once after file load ltc={Ltc:F3}", accepted);
        _lastAppliedLtcSeconds = accepted;
        RequestSyncEffective(accepted);
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
        if (!state.SyncEnabled || !state.IsMonitoring || state.IsPlaybackPaused || state.IsSeeking)
            return;
        if (_syncService.SeekState.HasPendingSeek)
            return;

        if (_rateRestorePending)
        {
            // T7: 一時停止中などで戻せなかった倍率を、評価の前に 1.0 へ戻す。
            if (!_effects.ApplyRateInstant(1.0))
                return;
            _lastAppliedRate = 1.0;
            _rateRestorePending = false;
        }

        double residualSeconds;
        double targetSeconds;
        if (state.Mode == SyncMode.Continue)
        {
            // T7: Continue は粗い同期判定と同じ素材位置と再生位置を使う。素材位置は
            // コーディネーターが 1 か所で出した値なので、残差は sync.evaluate の delta と一致する。
            if (_lastContinueFrame is not { CorrectionAllowed: true } frame)
                return;
            residualSeconds = frame.MediaPositionSeconds - frame.PlaybackSeconds;
            targetSeconds = frame.MediaPositionSeconds;
        }
        else
        {
            if (_effects.GetPlaybackSeconds == null) return;
            if (_effects.GetPlaybackSeconds() is not double playback || !double.IsFinite(playback))
                return;
            residualSeconds = ltcSeconds - playback;
            targetSeconds = ltcSeconds;
        }

        SyncCorrectionDecision decision = _correction.Evaluate(
            residualSeconds, targetSeconds, _effects.GetCorrectionMode(), _smoothAvailable, _getUtcNow());

        switch (decision.Action)
        {
            case SyncCorrectionActionType.SetRate:
                if (!_effects.ApplyRateInstant(decision.Rate))
                {
                    _smoothAvailable = false;
                    Log.Warning("Smooth 補正を使用できません（レート変更が拒否されました）。Jump への切替を検討してください");
                }
                else if (Math.Abs(decision.Rate - _lastAppliedRate) >= 0.0005)
                {
                    _lastAppliedRate = decision.Rate;
                    Log.Information(
                        "Smooth correction rate={Rate:F5} residualMs={ResidualMs:F1}",
                        decision.Rate, residualSeconds * 1000.0);
                }
                break;
            case SyncCorrectionActionType.Seek:
                // Smooth の倍率を Jump へ持ち込まない（shim 側では強制しない）。
                _effects.ApplyRateInstant(1.0);
                _lastAppliedRate = 1.0;
                if (_effects.SeekTo(decision.TargetSeconds))
                {
                    Log.Information(
                        "Jump correction seek target={Target:F3} residualMs={ResidualMs:F1}",
                        decision.TargetSeconds, residualSeconds * 1000.0);
                    _syncService.ReportSeekSent(decision.TargetSeconds);
                }
                break;
        }

        string status =
            !_smoothAvailable || _correction.SmoothUnavailable ? "Smooth 使用不可: Jump に切替"
            : _correction.SmoothDisabled ? "Smooth 補正なし（効かない）"
            : "";
        _effects.SetCorrectionStatus?.Invoke(status);
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
        _pendingSyncSeconds = null;
        if (!_signalLoss.ShouldSuppressSync)
            RequestSync(rawSeconds, frameEndTimestamp);
    }

    public void Tick(long nowMilliseconds)
    {
        ApplySignalLossAction(_signalLoss.Evaluate(nowMilliseconds, SignalContext()));
        RefreshDisplay();
        if (_pendingSyncSeconds is double pending)
        {
            // T2: サンプル時計が有効なら、保留値は生値とフレーム終端を持ち、
            // 使う時点の age で実効値を取り直す（off は従来どおり実効値を再送する）。
            if (_sampleClockEnabled && _pendingSyncFrameEndTimestamp > 0)
                RequestSync(_pendingSyncRawSeconds, _pendingSyncFrameEndTimestamp, "tick");
            else
                RequestSyncEffective(pending);
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

    private void ApplySignalLossAction(LtcSignalLossAction action)
    {
        if (action == LtcSignalLossAction.None || !_effects.GetContext().IsPlayerReady)
            return;
        bool pause = action == LtcSignalLossAction.Pause;
        _effects.SetSignalLossPaused(pause);
        LtcSyncContext state = _effects.GetContext();
        if (pause)
        {
            Log.Information(
                "LTC signal lost: playback paused timeoutMs={TimeoutMs} reason={Reason}",
                state.SignalLossTimeoutMilliseconds, _signalLoss.Reason);
            // D27: 保持（タイムコード停止）で止めるときは、停止位置を保持値へ 1 回だけ着地させる。
            // 無音（信号断）では着地先の値が無いので何もしない。
            if (_signalLoss.Reason == LtcSignalLossReason.TimecodeHeld)
                ReapplyHeldValueOnPause();
        }
        else
        {
            Log.Information("LTC signal restored: playback resumed resumeFrames={ResumeFrames}", state.SignalResumeFrames);
        }
    }

    /// <summary>
    /// D27: 保持で一時停止したときの 1 回の着地。保持値へシークし、フレームが保持時刻に
    /// 対応した位置で止まるようにする。同期エンジンのデバウンス・保留状態には依存しない
    /// （停止時の 1 回だけ）。
    /// D27-d: 保持値は「保持として届いた最後の値（Duplicate）」を使う。保持直前の受理値は
    /// 1 フレーム手前で止まることがある（受領が 1 フレーム遅れる／Jump を適用しない場合）。
    /// </summary>
    private void ReapplyHeldValueOnPause()
    {
        double? held = _lastHeldEffectiveSeconds ?? _lastAcceptedLtcSeconds;
        if (held is not double heldSeconds || _effects.SeekTo == null)
            return;
        LtcSyncContext state = _effects.GetContext();
        if (!state.IsMonitoring || !state.SyncEnabled || state.IsSeeking)
            return;

        double target;
        if (state.Mode == SyncMode.Continue)
        {
            TimelineQueryResult result = _playlist.FindTrackAtTimelinePosition(heldSeconds);
            if (result.Status != TimelineQueryStatus.OnTrack)
                return;
            target = result.MediaPositionSeconds;
        }
        else
        {
            target = Math.Clamp(heldSeconds, 0, state.DurationSeconds);
        }

        if (_effects.SeekTo(target))
        {
            _syncService.ReportSeekSent(target);
            Log.Information(
                "LTC timecode held: landing seek issued target={Target:F3} ltc={Ltc:F3}", target, heldSeconds);
        }
    }

    private SyncRequestResult ApplySync(double seconds, bool gapDisplayOnly = false)
    {
        _lastContinueFrame = null;
        LtcSyncContext state = _effects.GetContext();
        if (!state.IsPlayerReady || !state.IsMonitoring || !state.SyncEnabled ||
            state.IsSeeking || _signalLoss.ShouldSuppressSync)
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
                    _smoothAvailable = true;
                    // T9: 着地（ロード成立）から 1.0 秒の補正窓を開く。ResetCorrection の後に置くこと
                    // （Reset は前の窓を捨てる）。
                    _correction.NotifyLanding(_getUtcNow());
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
        if (exit.ShouldResumePlayback && !_signalLoss.IsPauseOwned && state.IsPlayerReady)
            _effects.ResumeGapPause();
        _effects.ClearGapFreezeFrame();
        _effects.RefreshCurrentVideoFrame();
        Log.Information("Gap state cleared for manual control syncEnabled={SyncEnabled} mode={Mode}",
            state.SyncEnabled, state.Mode);
        _effects.UpdateCurrentTrackLabel();
    }
}
