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

    private GapState _currentState = GapState.Inactive;
    private bool _pauseOwnedByGap;
    private bool _pauseOwnershipRecorded;
    private readonly TimeProvider _timeProvider;
    internal long CaptureAttemptId { get; private set; }
    // D21: 進入・再ロードの後に実際のフレームが 1 枚届くまでキャプチャを許可しない
    // （位置だけが先に目標へ動き、シーク前の絵を最終フレームとして固定するのを防ぐ）。
    internal bool FrameSeenSinceCapture { get; private set; } = true;

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

    public bool IsInactive => _currentState == GapState.Inactive;

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
        FrameSeenSinceCapture = true;
    }

    public void ResetAll()
    {
        Reset();
        CachedTrackId = null;
        CachedTargetSeconds = 0;
    }

    public void EnterFreezeCapture(Guid? trackId, double targetSeconds, string? filePath)
    {
        CaptureAttemptId++;
        _currentState = GapState.EnteringFreeze;
        StartedAt = _timeProvider.GetUtcNow().UtcDateTime;
        PendingTrackId = trackId;
        PendingTargetSeconds = targetSeconds;
        PendingPath = filePath;
        FrameSeenSinceCapture = false;
    }

    internal void NotifyFrameArrived() => FrameSeenSinceCapture = true;

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
        PendingTrackId = null;
        PendingTargetSeconds = 0;
        PendingPath = null;
    }

    public void ForceFreezeComplete()
    {
        _currentState = GapState.FreezeComplete;
        StartedAt = DateTime.MinValue;
        // タイムアウト時の表示は、確定済みの最終画像として再利用しない。
        ClearCachedFrameInfo();
        PendingTrackId = null;
        PendingTargetSeconds = 0;
        PendingPath = null;
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
    /// </summary>
    internal GapEnterAction DecideGapEnter(
        TimelineQueryResult result,
        GapBehavior gapBehavior,
        Guid? loadedTrackId,
        double currentVideoFps,
        double currentDurationSeconds)
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
                return BuildFreezeEnterAction(result, loadedTrackId, currentVideoFps, currentDurationSeconds);
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
        FrameSeenSinceCapture = true;
        ClearCachedFrameInfo();
        SetState(GapState.Inactive);
    }

    private GapEnterAction BuildFreezeEnterAction(
        TimelineQueryResult result,
        Guid? loadedTrackId,
        double currentVideoFps,
        double currentDurationSeconds)
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

        // target = 0 のとき（duration が frameSeconds 未満）はキャッシュ再利用せず再シークする
        if (target > 0 && CanReuseCachedFrame(previousTrackId, target, frameSeconds))
        {
            SetState(GapState.FreezeComplete);
            return new GapEnterAction(
                GapEnterActionType.UseCachedFrame,
                previousTrackId,
                target,
                duration,
                fps);
        }

        if (ShouldLoadPreviousTrackForGapFreeze(loadedTrackId, previousTrackId))
        {
            return new GapEnterAction(
                GapEnterActionType.LoadPreviousTrack,
                previousTrackId,
                target,
                duration,
                fps);
        }

        return new GapEnterAction(
            GapEnterActionType.SeekToFinalFrame,
            previousTrackId,
            target,
            duration,
            fps);
    }

    private void SetState(GapState state) => _currentState = state;
}
