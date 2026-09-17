namespace TimecodeSyncPlayer;

/// <summary>
/// Gapフリーズ状態のステートマシン。
/// Inactive: 通常再生中（Gap非アクティブ）
/// BlackFrameActive: ブラックフレーム描画中
/// EnteringFreeze: Gapフリーズ進入中（seek完了待ち）
/// WaitingForFrameStep: 最終画像のコピー完了待ち（旧状態名）
/// FreezeComplete: Gapフリーズ完了（最終フレーム固定）
/// ForceBlack: トラックなし・ブラック強制
/// </summary>
internal enum GapState
{
    Inactive,
    BlackFrameActive,
    EnteringFreeze,
    WaitingForFrameStep,
    FreezeComplete,
    ForceBlack
}

internal enum GapEnterActionType
{
    None,
    EnterBlackGap,
    EnterFreezeFromLastTrack,
    ForceBlack,
    UseCachedFrame,
    LoadPreviousTrack,
    SeekToFinalFrame,
    UseCurrentFrame,
    LoadNextTrackFirstFrame
}

internal sealed record GapEnterAction(
    GapEnterActionType Type,
    Guid? TrackId = null,
    double? TargetSeconds = null,
    double? DurationSeconds = null,
    double? Fps = null);

internal enum GapExitActionType
{
    None,
    ResumePlayback
}

internal sealed record GapExitAction(
    GapExitActionType Type,
    bool ShouldResumePlayback = true);

public sealed class GapFreezeHandler
{
    public const double TimeoutSec = 3.0;
    public const double EndAdvanceThresholdSec = 0.15;
    internal const double DefaultFallbackFps = 30.0;    // MainWindow・GapEnterCoordinator と共有
    // D21-b: 目標位置でないフレームが届いたときに、目標へ向けてシークをやり直す上限。
    public const int MaxSeekRetries = 2;

    private GapState _currentState = GapState.Inactive;
    private bool _pauseOwnedByGap;
    private bool _pauseOwnershipRecorded;
    private readonly TimeProvider _timeProvider;
    internal long CaptureAttemptId { get; private set; }
    // D21/D21-b: 進入・再ロードの後に「目標位置のフレーム」が届くまでキャプチャを許可しない。
    // フレーム位置は OutputEngine が取得したソースフレームの PTS（再生位置クエリより正確）。
    private volatile bool _frameSeenSinceCapture = true;
    // D21-b: 目標位置でないフレーム（シーク前の実行中フレーム）が届いたときの再シーク回数。
    private int _seekRetryCount;

    public GapFreezeHandler(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal GapState CurrentState
    {
        get => _currentState;
        set => _currentState = value;
    }

    public DateTime StartedAt { get; set; } = DateTime.MinValue;
    public Guid? PendingTrackId { get; set; }
    public double PendingTargetSeconds { get; set; }
    public string? PendingPath { get; set; }
    public DateTime LastReloadAt { get; set; } = DateTime.MinValue;
    public Guid? CachedTrackId { get; set; }
    public double CachedTargetSeconds { get; set; }

    /// <summary>
    /// D32: Cached が「捕捉済みの目標」を表すか（目標 0 も有効なので値では判定できない）。
    /// OnFreezeComplete で true、ClearCachedFrameInfo / ResetAll で false。
    /// </summary>
    public bool CachedTargetKnown { get; private set; }

    // D32: 3 秒のタイムアウトで確定を打ち切った後も、同じギャップに居る間は目標一致フレームの
    // 到着で確定し直せるように残す目標。目標 0（MediaIn 0 の先頭フレーム）も有効な目標なので、
    // 未設定は null で表す。Reset / 確定 / 新しい進入で解除する。
    public Guid? LateConfirmTrackId { get; private set; }
    public double? LateConfirmTargetSeconds { get; private set; }
    public string? LateConfirmPath { get; private set; }
    public bool HasLateConfirmTarget => LateConfirmTargetSeconds.HasValue;

    public bool IsInactive => _currentState == GapState.Inactive;

    /// <summary>進入・再ロードの後に「目標位置のフレーム」が届いたか（D21・D21-b）。</summary>
    internal bool FrameSeenSinceCapture => _frameSeenSinceCapture;

    /// <summary>D21-b: 再シークをまだ試せるか。</summary>
    internal bool CanRetrySeek => _seekRetryCount < MaxSeekRetries;

    internal int SeekRetryCount => _seekRetryCount;

    public void Reset()
    {
        CaptureAttemptId++;
        _currentState = GapState.Inactive;
        _pauseOwnedByGap = false;
        _pauseOwnershipRecorded = false;
        StartedAt = DateTime.MinValue;
        LastReloadAt = DateTime.MinValue;    // 追加
        PendingTrackId = null;
        PendingTargetSeconds = 0;
        PendingPath = null;
        ClearLateConfirmTarget();
        _frameSeenSinceCapture = true;
        _seekRetryCount = 0;
    }

    public void ResetAll()
    {
        Reset();
        CachedTrackId = null;
        CachedTargetSeconds = 0;
        CachedTargetKnown = false;
    }

    public void EnterFreezeCapture(Guid? trackId, double targetSeconds, string? filePath)
    {
        CaptureAttemptId++;
        _currentState = GapState.EnteringFreeze;
        StartedAt = _timeProvider.GetUtcNow().UtcDateTime;
        PendingTrackId = trackId;
        PendingTargetSeconds = targetSeconds;
        PendingPath = filePath;
        ClearLateConfirmTarget();
        _frameSeenSinceCapture = false;
        _seekRetryCount = 0;
    }

    /// <summary>
    /// D21-b (a): ロード中トラックが直前トラックと同じで、表示中の絵がすでに最終フレームのとき。
    /// 改めてシークせず、現在の絵をそのまま最終フレームとして確定する。
    /// </summary>
    public void EnterFreezeCaptureWithCurrentFrame(Guid? trackId, double targetSeconds, string? filePath)
    {
        EnterFreezeCapture(trackId, targetSeconds, filePath);
        _frameSeenSinceCapture = true;
    }

    /// <summary>D21-b: 目標位置のソースフレームが届いた（OutputEngine のフレーム位置で確認）。</summary>
    internal void NotifyFrameArrived() => _frameSeenSinceCapture = true;

    /// <summary>
    /// D21-b: 目標位置でないフレームが届いたため、目標へ向けてシークをやり直す。再びフレーム到着を待つ。
    /// 上限に達しているときは false（呼び出し側は現状のままタイムアウトへ委ねる）。
    /// </summary>
    internal bool TryBeginSeekRetry()
    {
        if (_seekRetryCount >= MaxSeekRetries)
            return false;
        _seekRetryCount++;
        _frameSeenSinceCapture = false;
        StartedAt = _timeProvider.GetUtcNow().UtcDateTime;
        return true;
    }

    public void EnterFreezeCaptureWithReload(Guid? trackId, double targetSeconds, string? filePath)
    {
        EnterFreezeCapture(trackId, targetSeconds, filePath);
        LastReloadAt = _timeProvider.GetUtcNow().UtcDateTime;
    }

    public void OnFreezeComplete(Guid? loadedTrackId)
    {
        _currentState = GapState.FreezeComplete;
        StartedAt = DateTime.MinValue;
        CachedTrackId = PendingTrackId ?? loadedTrackId;
        CachedTargetSeconds = PendingTargetSeconds;
        CachedTargetKnown = true;
        PendingTrackId = null;
        PendingTargetSeconds = 0;
        PendingPath = null;
        ClearLateConfirmTarget();
    }

    public void ForceFreezeComplete()
    {
        // D32: 打ち切った目標は、同じギャップに居る間だけ遅延確定のために残す
        // （届いたフレームで確定し直す。Cached には入れない = 最終画像として認定しない）。
        // 目標 0 も有効なので値ではなく「捕捉中だったか」で判定する。
        bool hadCapture = _currentState is GapState.EnteringFreeze or GapState.WaitingForFrameStep;
        _currentState = GapState.FreezeComplete;
        StartedAt = DateTime.MinValue;
        // タイムアウト時の表示は、確定済みの最終画像として再利用しない。
        ClearCachedFrameInfo();
        LateConfirmTrackId = hadCapture ? PendingTrackId : null;
        LateConfirmTargetSeconds = hadCapture ? PendingTargetSeconds : null;
        LateConfirmPath = hadCapture ? PendingPath : null;
        PendingTrackId = null;
        PendingTargetSeconds = 0;
        PendingPath = null;
    }

    /// <summary>
    /// D32: タイムアウト後に目標一致フレームが遅れて届いたとき、その目標の捕捉を開き直す。
    /// 呼び出し側は「届いた」ことを確認済みのフレーム位置で呼ぶ（FrameSeenSinceCapture を立てる）。
    /// </summary>
    public void ReopenCaptureForLateFrame()
    {
        if (LateConfirmTargetSeconds is not double target)
            return;
        Guid? trackId = LateConfirmTrackId ?? CachedTrackId;
        string? path = LateConfirmPath;
        EnterFreezeCapture(trackId, target, path);
        _frameSeenSinceCapture = true;
    }

    /// <summary>
    /// D32: 遅延確定の待ち受け中に、このフレーム位置が残した目標と一致するか（±2 フレーム）。
    /// 目標 0 でも成立する。
    /// </summary>
    public bool IsLateConfirmFrame(double positionSeconds, double fps)
    {
        if (LateConfirmTargetSeconds is not double target || !double.IsFinite(positionSeconds))
            return false;
        double frameSeconds = fps > 0 ? 1.0 / fps : 1.0 / DefaultFallbackFps;
        return Math.Abs(positionSeconds - target) <= frameSeconds * 2.0;
    }

    private void ClearLateConfirmTarget()
    {
        LateConfirmTrackId = null;
        LateConfirmTargetSeconds = null;
        LateConfirmPath = null;
    }

    public bool HasTimedOut() =>
        _currentState is GapState.EnteringFreeze or GapState.WaitingForFrameStep &&
        StartedAt != DateTime.MinValue &&
        _timeProvider.GetUtcNow().UtcDateTime - StartedAt > TimeSpan.FromSeconds(TimeoutSec);

    public bool ShouldStartFreezeCapture(GapBehavior gapBehavior) =>
        gapBehavior == GapBehavior.Freeze && _currentState == GapState.Inactive;

    public bool ShouldTransitionFromFreezeToBlack(GapBehavior gapBehavior) =>
        gapBehavior == GapBehavior.Black &&
        (_currentState is GapState.EnteringFreeze or GapState.WaitingForFrameStep or GapState.FreezeComplete);

    public bool ShouldTransitionFromBlackToFreeze(GapBehavior gapBehavior) =>
        gapBehavior == GapBehavior.Freeze && _currentState == GapState.BlackFrameActive;

    public bool ShouldRenderBlackForGapFreeze(Guid? previousTrackId) =>
        ContinueModePlaybackPolicy.ShouldRenderBlackForGapFreeze(previousTrackId);

    public bool ShouldLoadPreviousTrackForGapFreeze(Guid? loadedTrackId, Guid? previousTrackId) =>
        ContinueModePlaybackPolicy.ShouldLoadPreviousTrackForGapFreeze(loadedTrackId, previousTrackId);

    public bool CanReuseCachedFrame(Guid? trackId, double target, double frameSeconds) =>
        ContinueModePlaybackPolicy.CanReuseFrozenFrame(
            CachedTrackId, CachedTargetSeconds, trackId, target, frameSeconds);

    public void ClearCachedFrameInfo()
    {
        CachedTrackId = null;
        CachedTargetSeconds = 0;
        CachedTargetKnown = false;
    }

    /// <summary>
    /// D32: 新しい進入目標が、今のフリーズ画像（確定済み = Cached、捕捉中 = Pending）の目標と
    /// 異なるか。異なれば frozen を破棄してから新しい目標を捕捉する（前のギャップの絵が残るのを
    /// 防ぐ）。同じ目標の再進入では false（F-1 の周期再進入で frozen を捨てない）。
    /// 目標 0（MediaIn 0 の先頭フレーム）も有効な目標として扱う。
    /// </summary>
    public bool ShouldDiscardFrozenFrame(Guid? trackId, double targetSeconds, double frameSeconds)
    {
        if (!double.IsFinite(targetSeconds))
            return false;

        Guid? knownTrackId;
        double knownTarget;
        if (_currentState is GapState.EnteringFreeze or GapState.WaitingForFrameStep)
        {
            // 捕捉中は Pending と比べる（目標 0 も含めて常に有効）。
            knownTrackId = PendingTrackId;
            knownTarget = PendingTargetSeconds;
        }
        else if (CachedTargetKnown)
        {
            knownTrackId = CachedTrackId;
            knownTarget = CachedTargetSeconds;
        }
        else
        {
            return false;
        }

        if (knownTrackId != trackId)
            return true;
        double tolerance = frameSeconds > 0 ? frameSeconds * 0.5 : 0.0;
        return Math.Abs(targetSeconds - knownTarget) > tolerance;
    }

    internal void RecordPauseOwnership(bool wasPlaybackPaused)
    {
        if (_pauseOwnershipRecorded)
            return;

        _pauseOwnershipRecorded = true;
        _pauseOwnedByGap = !wasPlaybackPaused;
    }

    /// <summary>
    /// Gap 進入時のアクションを決定する。MainWindow はこの戻り値に従って mpv 操作を行う。
    /// loadedPositionSeconds は現在ロード中のトラックの再生位置（取得できないときは null）。
    /// </summary>
    internal GapEnterAction DecideGapEnter(
        TimelineQueryResult result,
        GapBehavior gapBehavior,
        Guid? loadedTrackId,
        double currentVideoFps,
        double currentDurationSeconds,
        double? loadedPositionSeconds = null)
    {
        if (ShouldTransitionFromFreezeToBlack(gapBehavior))
        {
            CancelFreezeCaptureForTransition();
        }
        else if (ShouldTransitionFromBlackToFreeze(gapBehavior))
        {
            SetState(GapState.Inactive);
        }
        else if (gapBehavior == GapBehavior.Freeze &&
                 CurrentState is GapState.FreezeComplete or GapState.ForceBlack)
        {
            SetState(GapState.Inactive);
        }

        if (gapBehavior == GapBehavior.Black)
        {
            if (CurrentState == GapState.Inactive)
            {
                SetState(GapState.BlackFrameActive);
                return new GapEnterAction(GapEnterActionType.EnterBlackGap);
            }
        }
        else
        {
            if (CurrentState == GapState.Inactive)
            {
                return BuildFreezeEnterAction(result, loadedTrackId, currentVideoFps, currentDurationSeconds,
                    loadedPositionSeconds);
            }
        }
        return new GapEnterAction(GapEnterActionType.None);
    }

    /// <summary>
    /// NoTracks 状態のアクションを決定する。
    /// </summary>
    internal GapEnterAction DecideNoTracksEnter(GapBehavior gapBehavior, Guid? loadedTrackId)
    {
        if (ShouldTransitionFromFreezeToBlack(gapBehavior))
            CancelFreezeCaptureForTransition();
        else if (gapBehavior == GapBehavior.Freeze && loadedTrackId.HasValue &&
                 CurrentState is GapState.BlackFrameActive or GapState.ForceBlack)
            SetState(GapState.Inactive);

        if (CurrentState != GapState.Inactive)
            return new GapEnterAction(GapEnterActionType.None);

        if (gapBehavior == GapBehavior.Freeze && loadedTrackId.HasValue)
        {
            return new GapEnterAction(GapEnterActionType.EnterFreezeFromLastTrack);
        }

        SetState(GapState.ForceBlack);
        return new GapEnterAction(GapEnterActionType.ForceBlack);
    }

    /// <summary>
    /// OnTrack 状態に戻った時のアクション。
    /// </summary>
    internal GapExitAction PeekGapExit() => IsInactive
        ? new GapExitAction(GapExitActionType.None)
        : new GapExitAction(GapExitActionType.ResumePlayback, _pauseOwnedByGap);

    internal GapExitAction DecideGapExit()
    {
        if (CurrentState == GapState.Inactive)
            return new GapExitAction(GapExitActionType.None);

        bool shouldResumePlayback = _pauseOwnedByGap;
        Reset();
        return new GapExitAction(GapExitActionType.ResumePlayback, shouldResumePlayback);
    }

    // Changing the output must cancel capture work without losing ownership of its pause.
    private void CancelFreezeCaptureForTransition()
    {
        StartedAt = DateTime.MinValue;
        LastReloadAt = DateTime.MinValue;
        PendingTrackId = null;
        PendingTargetSeconds = 0;
        PendingPath = null;
        _frameSeenSinceCapture = true;
        _seekRetryCount = 0;
        ClearCachedFrameInfo();
        SetState(GapState.Inactive);
    }

    private GapEnterAction BuildFreezeEnterAction(
        TimelineQueryResult result,
        Guid? loadedTrackId,
        double currentVideoFps,
        double currentDurationSeconds,
        double? loadedPositionSeconds)
    {
        PlaylistTrack? previousTrack = result.PreviousTrack;
        Guid? previousTrackId = previousTrack?.Id;

        if (!previousTrackId.HasValue)
        {
            // D22: 先頭オフセット領域（前トラックなし）の Freeze は、次のトラックの
            // 冒頭フレーム（MediaIn）を保持する。次のトラックが無い場合だけ黒。
            PlaylistTrack? nextTrack = result.NextTrack;
            if (nextTrack != null)
            {
                double nextFps = nextTrack.FrameRate ?? (currentVideoFps > 0 ? currentVideoFps : DefaultFallbackFps);
                double nextFrameSeconds = 1.0 / nextFps;
                double nextDuration = (nextTrack.MediaOut ?? nextTrack.MediaDuration).TotalSeconds;
                if (nextDuration <= 0)
                {
                    Serilog.Log.Warning("GapFreezeHandler: nextTrack {TrackId} has duration <= 0, falling back to currentDurationSeconds={Duration:F3}", nextTrack.Id, currentDurationSeconds);
                    nextDuration = currentDurationSeconds;
                }
                double nextTarget = Math.Max(0, nextTrack.MediaIn.TotalSeconds);

                if (CanReuseCachedFrame(nextTrack.Id, nextTarget, nextFrameSeconds))
                {
                    SetState(GapState.FreezeComplete);
                    return new GapEnterAction(
                        GapEnterActionType.UseCachedFrame,
                        nextTrack.Id,
                        nextTarget,
                        nextDuration,
                        nextFps);
                }

                return new GapEnterAction(
                    GapEnterActionType.LoadNextTrackFirstFrame,
                    nextTrack.Id,
                    nextTarget,
                    nextDuration,
                    nextFps);
            }

            ClearCachedFrameInfo();
            SetState(GapState.ForceBlack);
            return new GapEnterAction(GapEnterActionType.ForceBlack);
        }

        // fps: トラックの FrameRate → currentVideoFps → デフォルト 30fps の順にフォールバック
        double fps = previousTrack!.FrameRate ?? (currentVideoFps > 0 ? currentVideoFps : DefaultFallbackFps);
        double frameSeconds = 1.0 / fps;

        // duration: MediaOut → MediaDuration → currentDurationSeconds の順にフォールバック
        double duration = (previousTrack.MediaOut ?? previousTrack.MediaDuration).TotalSeconds;
        if (duration <= 0)
        {
            Serilog.Log.Warning("GapFreezeHandler: previousTrack {TrackId} has duration <= 0, falling back to currentDurationSeconds={Duration:F3}", previousTrackId, currentDurationSeconds);
            duration = currentDurationSeconds;
        }

        double target = duration > 0 ? Math.Max(0, duration - frameSeconds) : 0;

        // D21-b: 直前トラックが未ロード（または別トラック）なら、その最終フレームを
        // 一時停止で読み込んでから最終フレームへシークする。
        if (ShouldLoadPreviousTrackForGapFreeze(loadedTrackId, previousTrackId))
        {
            return new GapEnterAction(
                GapEnterActionType.LoadPreviousTrack,
                previousTrackId,
                target,
                duration,
                fps);
        }

        // ロード中トラックが直前トラックと同じ。位置が最終フレーム ±1 フレームなら
        // すでに表示中の絵が最終フレームなので、改めてシークせず現在の絵を確定する。
        bool atFinalFrame = target > 0 &&
            loadedPositionSeconds.HasValue &&
            double.IsFinite(loadedPositionSeconds.Value) &&
            Math.Abs(loadedPositionSeconds.Value - target) <= frameSeconds;

        return new GapEnterAction(
            atFinalFrame ? GapEnterActionType.UseCurrentFrame : GapEnterActionType.SeekToFinalFrame,
            previousTrackId,
            target,
            duration,
            fps);
    }

    private void SetState(GapState state) => _currentState = state;
}
