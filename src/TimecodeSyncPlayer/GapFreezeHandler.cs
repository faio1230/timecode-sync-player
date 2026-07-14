namespace TimecodeSyncPlayer;

/// <summary>
/// Gapフリーズ状態のステートマシン。
/// Inactive: 通常再生中（Gap非アクティブ）
/// BlackFrameActive: ブラックフレーム描画中
/// EnteringFreeze: Gapフリーズ進入中（seek完了待ち）
/// WaitingForFrameStep: frame-step実行待ち
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
    SeekToFinalFrame
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

internal sealed record GapExitAction(GapExitActionType Type);

public sealed class GapFreezeHandler
{
    public const double TimeoutSec = 3.0;
    public const double EndAdvanceThresholdSec = 0.15;
    internal const double DefaultFallbackFps = 30.0;    // MainWindow・GapEnterCoordinator と共有

    private GapState _currentState = GapState.Inactive;

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
        _currentState = GapState.Inactive;
        StartedAt = DateTime.MinValue;
        LastReloadAt = DateTime.MinValue;    // 追加
        PendingTrackId = null;
        PendingTargetSeconds = 0;
        PendingPath = null;
    }

    public void ResetAll()
    {
        Reset();
        CachedTrackId = null;
        CachedTargetSeconds = 0;
    }

    public void EnterFreezeCapture(Guid? trackId, double targetSeconds, string? filePath)
    {
        _currentState = GapState.EnteringFreeze;
        StartedAt = DateTime.UtcNow;
        PendingTrackId = trackId;
        PendingTargetSeconds = targetSeconds;
        PendingPath = filePath;
    }

    public void EnterFreezeCaptureWithReload(Guid? trackId, double targetSeconds, string? filePath)
    {
        EnterFreezeCapture(trackId, targetSeconds, filePath);
        LastReloadAt = DateTime.UtcNow;
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
        CachedTrackId = PendingTrackId;              // Pending から引き継ぐ
        CachedTargetSeconds = PendingTargetSeconds;   // Pending から引き継ぐ
        PendingTrackId = null;
        PendingTargetSeconds = 0;
        PendingPath = null;
    }

    public bool HasTimedOut() =>
        _currentState is GapState.EnteringFreeze or GapState.WaitingForFrameStep &&
        StartedAt != DateTime.MinValue &&
        DateTime.UtcNow - StartedAt > TimeSpan.FromSeconds(TimeoutSec);

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
            SetState(GapState.Inactive);
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
    internal GapExitAction DecideGapExit()
    {
        if (CurrentState == GapState.Inactive)
            return new GapExitAction(GapExitActionType.None);

        Reset();
        return new GapExitAction(GapExitActionType.ResumePlayback);
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
