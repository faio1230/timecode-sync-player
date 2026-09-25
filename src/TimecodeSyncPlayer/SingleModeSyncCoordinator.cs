using Serilog;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer;

/// <summary>
/// Single モード（1トラック内絶対シーク）の同期判定・シーク発行を担う。
/// MainWindow.ApplySingleModeSync から抽出。判定条件・実行順序・早期return・
/// ログテンプレートは抽出前と完全に一致させること。
/// </summary>
internal sealed class SingleModeSyncCoordinator
{
    private readonly TimecodeSyncService _syncService;
    private readonly SingleModeSyncEffects _effects;

    // v0.5.2 段 2e: 境界ホールドの状態（終端ホールドと端へのシークの記録）。
    private readonly BoundaryHoldState _boundary = new();

    /// <summary>D35-b: 終端ホールド中か（保持値への明示着地を抑止する判定に使う）。</summary>
    public bool IsBoundaryHeld => _boundary.IsHeld;

    public SingleModeSyncCoordinator(
        TimecodeSyncService syncService,
        SingleModeSyncEffects effects)
    {
        _syncService = syncService;
        _effects = effects;
    }

    public SyncRequestResult Apply(double ltcSeconds)
    {
        // 0.4.5-A フェーズ 1: shadow は trace 有効時だけ読む（無効時は従来どおり位置を読まない）。
        bool traceEnabled = OutputTrace.Current.IsEnabled;

        // During a native seek, time-pos can still be the synthetic requested target.
        // Do not let it settle the pending seek or complete file-load stability checks.
        if (_effects.IsNativeSeeking?.Invoke() == true)
        {
            if (traceEnabled)
            {
                SyncPositionRead shadowRead = _effects.ReadPosition();
                if (shadowRead.Succeeded)
                {
                    SyncPlaybackState shadowState = _effects.BuildPlaybackState(shadowRead.PlaybackSeconds);
                    _syncService.RecordPositionShadow(ltcSeconds, shadowState, shadowRead.Sample, "native-seeking");
                }
            }
            return SyncRequestResult.Deferred;
        }

        SyncPositionRead read = _effects.ReadPosition();
        if (!read.Succeeded) return SyncRequestResult.Deferred;
        double playbackSeconds = read.PlaybackSeconds;

        SyncPlaybackState state = _effects.BuildPlaybackState(playbackSeconds);
        // 位置サンプルは秒と同じ照会の結果。shadow は trace 有効時だけ渡す。
        PlaybackPositionSample? positionSample = traceEnabled ? read.Sample : null;

        if (_syncService.IsLoadingFile && _effects.GetTotalRenderedFrames != null &&
            !_syncService.TryMarkFileLoaded(playbackSeconds, _effects.GetTotalRenderedFrames()))
            return SyncRequestResult.Deferred;

        // D33: 範囲外の LTC（D29 の clamp 後は clipIn/clipOut に貼り付く）で再生位置が端に
        // 達したら、シークも補正もせず終端ホールド（一時停止＋ラッチ）。LTC が許容分だけ
        // 内側へ戻ったら解除して追従を再開する。
        if (ApplyClipBoundaryHold(ltcSeconds, playbackSeconds, state))
            return SyncRequestResult.Complete;

        SyncDecision decision = _syncService.EvaluateDecision(ltcSeconds, state, positionSample);
        // None の decision は TargetSeconds=0 のため、シーク要求として渡さない（D20-b (ii)）。
        double requestedTarget = decision.Action == SyncActionType.Seek ? decision.TargetSeconds : double.NaN;
        bool suppressSeek = _syncService.ShouldSuppressSeek(playbackSeconds, decision.ToleranceSeconds,
            requestedTarget);
        // D37-b: シーク中・着地未確認のフレームでは、粗い判定も補正も評価しない。
        if (decision.PositionUntrusted)
            return SyncRequestResult.Deferred;
        if (decision.Action != SyncActionType.Seek)
            // D37-a: ゲートが Seek を保留している間は要求を Deferred のまま維持し、
            // 次の評価（フレーム／Tick）で窓が埋まったら発行する。
            return decision.GateDeferred || _syncService.SeekState.HasPendingSeek
                ? SyncRequestResult.Deferred : SyncRequestResult.Complete;

        if (suppressSeek)
        {
            Log.Debug(
                "Timecode sync seek suppressed pendingTarget={PendingTarget:F3} playback={Playback:F3} ltc={Ltc:F3} requestedTarget={RequestedTarget:F3} tolerance={Tolerance:F4}",
                _syncService.SeekState.TargetSeconds, playbackSeconds, ltcSeconds,
                decision.TargetSeconds, decision.ToleranceSeconds);
            return SyncRequestResult.Deferred;
        }

        if (_syncService.IsDebounced())
            return SyncRequestResult.Deferred;

        bool success = _effects.SeekTo(decision.TargetSeconds);
        if (success)
        {
            _syncService.ReportSeekSent(decision.TargetSeconds);
            NoteBoundarySeek(decision.TargetSeconds, state);
        }
        Log.Information(
            "Timecode sync seek ltc={Ltc:F3} playback={Playback:F3} target={Target:F3} delta={Delta:F3} tolerance={Tolerance:F4} videoFps={VideoFps:F3} timecodeFps={TimecodeFps:F3} defaultVideoFps={DefaultVideoFps} defaultTimecodeFps={DefaultTimecodeFps} success={Success}",
            ltcSeconds, playbackSeconds, decision.TargetSeconds, decision.DeltaSeconds,
            decision.ToleranceSeconds, decision.VideoFpsUsed, decision.TimecodeFpsUsed,
            decision.UsedDefaultVideoFps, decision.UsedDefaultTimecodeFps, success);
        return success ? SyncRequestResult.Complete : SyncRequestResult.Deferred;
    }

    /// <summary>
    /// D33: 保持（Duplicate）フレームでも終端ホールド／解除を評価する。保持中は通常の同期評価が
    /// 走らない（シークもしない）ため、境界へ着地したあとに LTC が止まった場合の停止はここで行う。
    /// </summary>
    public bool ApplyClipBoundaryHoldOnly(double ltcSeconds)
    {
        if (_effects.IsNativeSeeking?.Invoke() == true)
            return _boundary.IsHeld;

        SyncPositionRead read = _effects.ReadPosition();
        if (!read.Succeeded)
            return _boundary.IsHeld;
        double playbackSeconds = read.PlaybackSeconds;

        SyncPlaybackState state = _effects.BuildPlaybackState(playbackSeconds);
        if (_syncService.IsLoadingFile && _effects.GetTotalRenderedFrames != null &&
            !_syncService.TryMarkFileLoaded(playbackSeconds, _effects.GetTotalRenderedFrames()))
            return _boundary.IsHeld;

        return ApplyClipBoundaryHold(ltcSeconds, playbackSeconds, state);
    }

    /// <summary>
    /// v0.5.3 段 3c: できごとで境界ホールドのラッチを消す（§6 の 1）。消すのはラッチ
    /// （ホールドと端へのシークの記録）だけで、一時停止は変えない（SetEndHold を呼ばない。
    /// 止まっている映像は利用者の再生で動く）。解除のできごと（BoundaryHoldReleased）も
    /// 出さない（それは LTC がクリップへ戻ったときの解除）。
    /// </summary>
    internal void OnLifecycle(SyncLifecycleEvent evt)
    {
        switch (evt)
        {
            case SyncLifecycleEvent.SyncModeChanged:
            case SyncLifecycleEvent.SyncDisabled:
            case SyncLifecycleEvent.PlaybackStopped:
                ClearBoundaryLatches(evt);
                break;
        }
    }

    private void ClearBoundaryLatches(SyncLifecycleEvent evt)
    {
        bool hadLatch = _boundary.IsHeld || _boundary.Seek is not null;
        _boundary.ClearHeld();
        _boundary.ClearSeek();
        if (hadLatch)
            Log.Information("Single mode: clip boundary hold cleared by {Event}", evt);
    }

    /// <summary>
    /// D33: 範囲外 LTC の端での終端ホールド。true を返したら呼び出し側はシーク・判定へ進まない。
    /// 端に達する前（シークで着地する前）は false を返し、通常の着地シークに任せる。
    /// </summary>
    private bool ApplyClipBoundaryHold(double ltcSeconds, double playbackSeconds, SyncPlaybackState state)
    {
        if (!state.SyncEnabled || !state.HasCurrentTrack || !double.IsFinite(ltcSeconds))
            return false;

        (double clipIn, double clipOut) = SyncDecisionEngine.ClipRange(
            state.MediaInSeconds, state.MediaOutSeconds, state.DurationSeconds);
        // 尺が未確定（0 など）の間は端が決まらないため、ホールドしない。
        if (!double.IsFinite(clipOut) || !SeekBarUpdateState.IsUsableDuration(clipOut - clipIn))
            return false;

        double fps = state.VideoFps > 0 ? state.VideoFps
            : state.TimecodeFps > 0 ? state.TimecodeFps : 30.0;
        double boundaryTolerance = 2.0 / fps;

        bool belowIn = ltcSeconds < clipIn;
        bool aboveOut = ltcSeconds > clipOut;
        if (!belowIn && !aboveOut)
        {
            _boundary.ClearSeek();
            // 解除の余白（端から 2 フレーム）は、いま止まっている側の端にだけ効かせる。
            // 以前は両端に効かせていたため、出口で止まったまま LTC が入口ちょうど（clipIn）に
            // 戻ると、どちらの条件にも当たらず出口に取り残された（検証機の S-3、クリップ [10,30] で
            // LTC を 10.000 に戻した回）。
            if (_boundary.IsHeld)
            {
                bool heldAtOut = playbackSeconds >= (clipIn + clipOut) * 0.5;
                bool leftHeldEdge = heldAtOut
                    ? ltcSeconds <= clipOut - boundaryTolerance
                    : ltcSeconds >= clipIn + boundaryTolerance;
                if (leftHeldEdge)
                    ReleaseBoundaryHold("", ltcSeconds, playbackSeconds, clipIn, clipOut);
            }
            return _boundary.IsHeld;
        }

        // 端に「着いた」: 端の ±2 フレーム、またはその端へのシークを出した後で端の外側。
        // 以前は外側を無条件に「着いた」としていたため、読み込み直後（位置 1.0）に LTC が入口
        // （10.0）より手前に来ると、入口へシークせずに 1.0 の絵でホールドしていた（検証機の S-2）。
        bool atOut = aboveOut && playbackSeconds >= clipOut - boundaryTolerance &&
            (playbackSeconds <= clipOut + boundaryTolerance || BoundarySeekSentTo(clipOut, boundaryTolerance));
        bool atIn = belowIn && playbackSeconds <= clipIn + boundaryTolerance &&
            (playbackSeconds >= clipIn - boundaryTolerance || BoundarySeekSentTo(clipIn, boundaryTolerance));
        if (!atOut && !atIn)
        {
            // 端に居ない（トラック差し替え後のロード直後など）。古いラッチを解除して、
            // 通常の着地シークで新しい端へ向かわせる。
            if (_boundary.IsHeld)
                ReleaseBoundaryHold(" (playback left the boundary)", ltcSeconds, playbackSeconds, clipIn, clipOut);
            return false;
        }

        if (!_boundary.IsHeld)
        {
            _boundary.MarkHeld();
            _effects.SetEndHold?.Invoke(true);
            Log.Information(
                "Single mode: clip boundary hold ltc={Ltc:F3} playback={Playback:F3} clip=[{In:F3},{Out:F3}]",
                ltcSeconds, playbackSeconds, clipIn, clipOut);
        }
        return true;
    }

    /// <summary>
    /// D35-b: 境界ホールドの解除。解除と同時に保留シーク状態と保持着地のラッチを解除する
    /// （ホールド中に残った端への pending が、新しい範囲内 LTC への着地シークを抑止するのを防ぐ）。
    /// </summary>
    private bool BoundarySeekSentTo(double edge, double tolerance) =>
        _boundary.Seek is { } seek && Math.Abs(seek.Target - edge) <= tolerance &&
        seek.Epoch == _syncService.FileLoadEpoch;

    /// <summary>端へのシーク（範囲外 LTC の着地先）を出したことを覚える。</summary>
    private void NoteBoundarySeek(double targetSeconds, SyncPlaybackState state)
    {
        (double clipIn, double clipOut) = SyncDecisionEngine.ClipRange(
            state.MediaInSeconds, state.MediaOutSeconds, state.DurationSeconds);
        double fps = state.VideoFps > 0 ? state.VideoFps
            : state.TimecodeFps > 0 ? state.TimecodeFps : 30.0;
        double tolerance = 2.0 / fps;
        double? edge = Math.Abs(targetSeconds - clipIn) <= tolerance ? clipIn
            : double.IsFinite(clipOut) && Math.Abs(targetSeconds - clipOut) <= tolerance ? clipOut
            : null;
        _boundary.NoteSeek(edge, _syncService.FileLoadEpoch);
    }

    private void ReleaseBoundaryHold(string suffix, double ltcSeconds, double playbackSeconds,
        double clipIn, double clipOut)
    {
        _boundary.ClearHeld();
        _boundary.ClearSeek();
        _effects.SetEndHold?.Invoke(false);
        _effects.OnBoundaryHoldReleased?.Invoke();
        Log.Information(
            "Single mode: clip boundary hold released{Suffix} ltc={Ltc:F3} playback={Playback:F3} clip=[{In:F3},{Out:F3}]",
            suffix, ltcSeconds, playbackSeconds, clipIn, clipOut);
    }

    /// <summary>
    /// v0.5.2 段 0: ラッチが立っているかの読み取り専用の写し（特性テスト用。状態は変えない）。
    /// boundarySeekTarget は「端へのシークの記録が、いまの読み込みに対して有効か」（読み込み番号が
    /// 一致するときだけ BoundarySeekSentTo が参照する）。
    /// </summary>
    internal IReadOnlyDictionary<string, bool> LatchSnapshot() => new Dictionary<string, bool>
    {
        ["clipBoundaryHeld"] = _boundary.IsHeld,
        ["boundarySeekTarget"] = _boundary.Seek is { } seek &&
            seek.Epoch == _syncService.FileLoadEpoch,
    };
}

/// <summary>
/// <see cref="SingleModeSyncCoordinator"/> が使用する副作用デリゲート群。
/// MainWindow のフィールド・メソッドをフェイク可能な形で注入する。
/// </summary>
internal sealed record SingleModeSyncEffects(
    // v0.5.1: 再生位置（秒）と位置サンプルを同じ 1 回の照会で返す。
    Func<SyncPositionRead> ReadPosition,
    Func<double, SyncPlaybackState> BuildPlaybackState,
    Func<double, bool> SeekTo,
    Func<long>? GetTotalRenderedFrames = null,
    Func<bool>? IsNativeSeeking = null,
    // D33: 終端ホールドの pause/resume（true = 端で一時停止、false = 解除して再開）。
    Action<bool>? SetEndHold = null,
    // D35-b: 終端ホールドの解除通知。保留シーク状態と保持着地のラッチを解除する。
    Action? OnBoundaryHoldReleased = null);
