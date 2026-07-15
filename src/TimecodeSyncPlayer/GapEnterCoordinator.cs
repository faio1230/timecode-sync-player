using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// Continue モードの Gap 進入 / NoTracks 進入時の副作用（一時停止・シーク・ブラック/フリーズ描画・
/// 前トラック再ロード・状態更新）を担う。MainWindow の
/// EnterBlackGap / EnterForceBlack / StartGapFreezeCaptureForCurrentTrack / EnterNoTracksFreeze /
/// LoadPreviousTrackFinalFrameForGapFreeze / HandleNoTracksSync から抽出。
/// 判定条件・実行順序・早期return・ログテンプレート・_endAdvanceTriggered の設定箇所は
/// 抽出前と完全に一致させること。
/// GapFreezeHandler は具象注入（テスト済みクラス）。その他の副作用は <see cref="GapEnterEffects"/> のデリゲート経由。
/// </summary>
internal sealed class GapEnterCoordinator
{
    private readonly GapFreezeHandler _gapFreezeHandler;
    private readonly GapEnterEffects _effects;

    public GapEnterCoordinator(GapFreezeHandler gapFreezeHandler, GapEnterEffects effects)
    {
        _gapFreezeHandler = gapFreezeHandler;
        _effects = effects;
    }

    public void EnterBlackGap()
    {
        _effects.ResetEndAdvanceTriggered();
        _effects.PauseForGap();
        _effects.ApplyPauseState(true);
        _effects.RenderBlack();
        Log.Information("Continue mode: entered gap, rendering black frame");
    }

    public void EnterForceBlack()
    {
        _effects.ResetEndAdvanceTriggered();
        _effects.ClearGapFreezeFrame();
        _effects.PauseForGap();
        _effects.ApplyPauseState(true);
        _effects.RenderBlack();
        Log.Information("Continue mode: gap, forcing black frame");
    }

    public void StartGapFreezeCaptureForCurrentTrack(TimelineQueryResult result, GapEnterAction action)
    {
        _effects.ResetEndAdvanceTriggered();
        _effects.PauseForGap();
        _effects.ApplyPauseState(true);

        PlaylistTrack? previousTrack = result.PreviousTrack;
        double target = action.TargetSeconds ?? 0;
        Guid? previousTrackId = action.TrackId ?? previousTrack?.Id;
        Guid? loadedTrackId = _effects.GetLoadedTrackId();

        if (target <= 0)
        {
            Log.Information("Continue mode: gap freeze activated, holding current frame because duration is unavailable");
            _gapFreezeHandler.OnFreezeComplete(loadedTrackId);
            _effects.RenderGapFreeze();
            return;
        }

        double duration = action.DurationSeconds ?? _effects.GetDuration();
        double currentFps = _effects.GetFps();
        double fps = action.Fps ?? (currentFps > 0 ? currentFps : GapFreezeHandler.DefaultFallbackFps);
        bool seekSuccess = _effects.SeekTo(target);
        if (seekSuccess)
        {
            _gapFreezeHandler.EnterFreezeCapture(previousTrackId ?? loadedTrackId, target, previousTrack?.FilePath);
            Log.Information(
                "Continue mode: entering gap freeze, waiting for final frame target={Target:F3} duration={Duration:F3} fps={Fps:F3}",
                target, duration, fps);
        }
        else
        {
            Log.Warning("Continue mode: gap freeze final-frame seek failed, holding current frame");
            _gapFreezeHandler.ForceFreezeComplete();
        }
    }

    public void EnterNoTracksFreeze()
    {
        _effects.PauseForGap();
        _effects.ApplyPauseState(true);

        (int durRc, double duration) = _effects.GetMpvDuration();
        if (durRc == 0 && duration > 0)
        {
            double currentFps = _effects.GetFps();
            double fps = currentFps > 0 ? currentFps : GapFreezeHandler.DefaultFallbackFps;
            double frameSeconds = 1.0 / fps;
            double target = Math.Max(0, duration - frameSeconds);
            bool seekSuccess = _effects.SeekTo(target);
            if (seekSuccess)
            {
                _gapFreezeHandler.EnterFreezeCapture(_effects.GetLoadedTrackId(), target, null);
                Log.Information("Continue mode: no tracks, entering gap freeze target={Target:F3} duration={Duration:F3}", target, duration);
            }
            else
            {
                _gapFreezeHandler.CurrentState = GapState.ForceBlack;
                _effects.RenderBlack();
                Log.Warning("Continue mode: no tracks, gap freeze seek failed");
            }
        }
        else
        {
            _gapFreezeHandler.CurrentState = GapState.ForceBlack;
            _effects.RenderBlack();
        }
        Log.Information("Continue mode: no tracks, freezing last frame");
    }

    public void LoadPreviousTrackFinalFrameForGapFreeze(PlaylistTrack previousTrack, double target, double duration, double fps)
    {
        _effects.ResetEndAdvanceTriggered();
        if (!_effects.IsMpvReady())
            return;

        GapLoadCommandResult commandResult = _effects.LoadPausedAt(previousTrack.FilePath, target);

        if (commandResult.LoadRc != 0)
        {
            Log.Warning(
                "Continue mode: gap freeze previous-track load failed track={Track} target={Target:F3} loadRc={LoadRc} pauseRc={PauseRc}",
                previousTrack.Name, target, commandResult.LoadRc, commandResult.PauseRc);
            _gapFreezeHandler.ForceFreezeComplete();
            return;
        }

        _effects.SetLoadedTrackId(previousTrack.Id);

        _effects.ApplyPauseState(true);
        _effects.ResetPlayerStateForNewTrack();
        _effects.SetDuration(duration);
        _effects.SetFps(fps);
        _gapFreezeHandler.EnterFreezeCaptureWithReload(previousTrack.Id, target, previousTrack.FilePath);

        Log.Information(
            "Continue mode: loading previous track final frame for gap freeze track={Track} target={Target:F3} duration={Duration:F3} fps={Fps:F3} loadRc={LoadRc} pauseRc={PauseRc}",
            previousTrack.Name, target, duration, fps, commandResult.LoadRc, commandResult.PauseRc);
    }

    public void HandleNoTracks()
    {
        var action = _gapFreezeHandler.DecideNoTracksEnter(_effects.GetGapBehavior(), _effects.GetLoadedTrackId());

        switch (action.Type)
        {
            case GapEnterActionType.EnterFreezeFromLastTrack:
                EnterNoTracksFreeze();
                break;
            case GapEnterActionType.ForceBlack:
                EnterForceBlack();
                break;
        }
        _effects.UpdateCurrentTrackLabel();
    }
}

/// <summary>
/// <see cref="GapEnterCoordinator"/> が使用する副作用デリゲート群。
/// MainWindow のフィールド・メソッド（mpv ハンドルを閉じ込めた <see cref="GapPlaybackCommandExecutor"/> 呼び出し・
/// FrameRenderer 描画・PixelBufferManager・状態フィールド更新）をフェイク可能な形で注入する。
/// _endAdvanceTriggered / _loadedTrackId / _duration / _fps の更新は現行タイミングを保つため
/// デリゲート経由で行う。GapFreezeHandler の状態遷移は具象クラスへ直接委譲する。
/// </summary>
internal sealed record GapEnterEffects(
    Action ResetEndAdvanceTriggered,
    Action PauseForGap,
    Action<bool> ApplyPauseState,
    Action RenderBlack,
    Action RenderGapFreeze,
    Action ClearGapFreezeFrame,
    Func<double, bool> SeekTo,
    Func<(int rc, double duration)> GetMpvDuration,
    Func<bool> IsMpvReady,
    Func<string, double, GapLoadCommandResult> LoadPausedAt,
    Action ResetPlayerStateForNewTrack,
    Func<Guid?> GetLoadedTrackId,
    Action<Guid> SetLoadedTrackId,
    Func<double> GetDuration,
    Action<double> SetDuration,
    Func<double> GetFps,
    Action<double> SetFps,
    Func<GapBehavior> GetGapBehavior,
    Action UpdateCurrentTrackLabel);
