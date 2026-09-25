using System.Diagnostics;
using Serilog;
using TimecodeSyncPlayer.Output;

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
    private readonly GapPlayerMode _playerMode;

    public GapEnterCoordinator(
        GapFreezeHandler gapFreezeHandler,
        GapEnterEffects effects,
        GapPlayerMode playerMode = GapPlayerMode.Pause)
    {
        _gapFreezeHandler = gapFreezeHandler;
        _effects = effects;
        _playerMode = playerMode;
    }

    public void EnterBlackGap()
    {
        // U1 計測: Gap 切替時に UI スレッドで同期実行される副作用の所要。
        long started = Stopwatch.GetTimestamp();
        try
        {
            _effects.ResetEndAdvanceTriggered();
            ApplyGapPause();
            Log.Information("Continue mode: entered gap, rendering black frame");
        }
        finally
        {
            LogElapsed(nameof(EnterBlackGap), started);
        }
    }

    public void EnterForceBlack()
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            _effects.ResetEndAdvanceTriggered();
            _effects.ClearGapFreezeFrame();
            ApplyGapPause();
            Log.Information("Continue mode: gap, forcing black frame");
        }
        finally
        {
            LogElapsed(nameof(EnterForceBlack), started);
        }
    }

    public void StartGapFreezeCaptureForCurrentTrack(TimelineQueryResult result, GapEnterAction action)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            StartGapFreezeCaptureCore(result, action);
        }
        finally
        {
            LogElapsed(nameof(StartGapFreezeCaptureForCurrentTrack), started);
        }
    }

    private void StartGapFreezeCaptureCore(TimelineQueryResult result, GapEnterAction action)
    {
        _effects.ResetEndAdvanceTriggered();
        ApplyGapPause();

        PlaylistTrack? previousTrack = result.PreviousTrack;
        double target = action.TargetSeconds ?? 0;
        Guid? previousTrackId = action.TrackId ?? previousTrack?.Id;
        Guid? loadedTrackId = _effects.GetLoadedTrackId();

        if (target <= 0)
        {
            Log.Information("Continue mode: gap freeze activated, holding current frame because duration is unavailable");
            _gapFreezeHandler.ForceFreezeComplete();
            return;
        }

        double duration = action.DurationSeconds ?? _effects.GetDuration();
        double currentFps = _effects.GetFps();
        double fps = action.Fps ?? (currentFps > 0 ? currentFps : GapFreezeHandler.DefaultFallbackFps);
        Guid? captureTrackId = previousTrackId ?? loadedTrackId;
        // D32: 別の目標へ入り直すときは、前のフリーズ画像を破棄してから捕捉する。
        DiscardFrozenFrameIfTargetChanged(captureTrackId, target, fps);
        // D21-b: 目標フレームの到着確認はシークより先に始める。シーク完了フレームが
        // 進入直後に届いても「進入後のフレーム」として数えられるようにする。
        _gapFreezeHandler.EnterFreezeCapture(captureTrackId, target, previousTrack?.FilePath);
        bool seekSuccess = _effects.SeekTo(target);
        if (seekSuccess)
        {
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

    /// <summary>
    /// D21-b (a): ロード中トラックが直前トラックと同じで、表示中の絵がすでに最終フレームのとき。
    /// シークもロードもせず、進入だけを行って現在のフレームを最終フレームとして確定させる。
    /// </summary>
    public void CaptureCurrentFrameForGapFreeze(TimelineQueryResult result, GapEnterAction action)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            _effects.ResetEndAdvanceTriggered();
            ApplyGapPause();

            PlaylistTrack? previousTrack = result.PreviousTrack;
            double target = action.TargetSeconds ?? 0;
            Guid? previousTrackId = action.TrackId ?? previousTrack?.Id;

            if (target <= 0)
            {
                Log.Information("Continue mode: gap freeze activated, holding current frame because duration is unavailable");
                _gapFreezeHandler.ForceFreezeComplete();
                return;
            }

            double duration = action.DurationSeconds ?? _effects.GetDuration();
            double currentFps = _effects.GetFps();
            double fps = action.Fps ?? (currentFps > 0 ? currentFps : GapFreezeHandler.DefaultFallbackFps);
            Guid? captureTrackId = previousTrackId ?? _effects.GetLoadedTrackId();
            // D32: 別の目標へ入り直すときは、前のフリーズ画像を破棄してから捕捉する。
            DiscardFrozenFrameIfTargetChanged(captureTrackId, target, fps);
            _gapFreezeHandler.EnterFreezeCaptureWithCurrentFrame(
                captureTrackId, target, previousTrack?.FilePath);
            Log.Information(
                "Continue mode: gap freeze holds current frame at final position target={Target:F3} duration={Duration:F3} fps={Fps:F3}",
                target, duration, fps);
        }
        finally
        {
            LogElapsed(nameof(CaptureCurrentFrameForGapFreeze), started);
        }
    }

    public void EnterNoTracksFreeze()
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            ApplyGapPause();

            (int durRc, double duration) = _effects.GetPlayerDuration();
            if (durRc == 0 && duration > 0)
            {
                double currentFps = _effects.GetFps();
                double fps = currentFps > 0 ? currentFps : GapFreezeHandler.DefaultFallbackFps;
                double frameSeconds = 1.0 / fps;
                double target = Math.Max(0, duration - frameSeconds);
                Guid? captureTrackId = _effects.GetLoadedTrackId();
                // D32: 別の目標へ入り直すときは、前のフリーズ画像を破棄してから捕捉する。
                DiscardFrozenFrameIfTargetChanged(captureTrackId, target, fps);
                bool seekSuccess = _effects.SeekTo(target);
                if (seekSuccess)
                {
                    _gapFreezeHandler.EnterFreezeCapture(captureTrackId, target, null);
                    Log.Information("Continue mode: no tracks, entering gap freeze target={Target:F3} duration={Duration:F3}", target, duration);
                }
                else
                {
                    _gapFreezeHandler.CurrentState = GapState.ForceBlack;
                    Log.Warning("Continue mode: no tracks, gap freeze seek failed");
                }
            }
            else
            {
                _gapFreezeHandler.CurrentState = GapState.ForceBlack;
            }
            Log.Information("Continue mode: no tracks, freezing last frame");
        }
        finally
        {
            LogElapsed(nameof(EnterNoTracksFreeze), started);
        }
    }

    public void LoadNextTrackFirstFrameForGapFreeze(PlaylistTrack nextTrack, double target, double duration, double fps)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            _effects.ResetEndAdvanceTriggered();
            if (!_effects.IsPlayerReady())
                return;

            _gapFreezeHandler.RecordPauseOwnership(_effects.IsPlaybackPaused?.Invoke() ?? false);

            // D32: 別の目標へ入り直すときは、前のフリーズ画像を破棄してからロードする。
            DiscardFrozenFrameIfTargetChanged(nextTrack.Id, target, fps);

            GapLoadCommandResult commandResult = _effects.LoadPausedAt(nextTrack.FilePath, target);

            if (!commandResult.Load.Success)
            {
                Log.Warning(
                    "Continue mode: gap freeze next-track load failed track={Track} target={Target:F3} error={Error} pauseOk={PauseOk}",
                    nextTrack.Name, target, commandResult.Load.Error, commandResult.Pause.Success);
                _gapFreezeHandler.ForceFreezeComplete();
                return;
            }

            // v0.5.3 段 3g: 読み込みが成功した後、同期側の口（ロード中の印を立てない）を通す。
            _effects.BeginGapFreezeLoad?.Invoke("load-paused-at");

            _effects.SetLoadedTrackId(nextTrack.Id);

            _effects.ApplyPauseState(true);
            _effects.ResetPlayerStateForNewTrack();
            _effects.SetDuration(duration);
            _effects.SetFps(fps);
            _gapFreezeHandler.EnterFreezeCaptureWithReload(nextTrack.Id, target, nextTrack.FilePath);

            Log.Information(
                "Continue mode: loading next track first frame for gap freeze track={Track} target={Target:F3} duration={Duration:F3} fps={Fps:F3} loadOk={LoadOk} pauseOk={PauseOk}",
                nextTrack.Name, target, duration, fps, commandResult.Load.Success, commandResult.Pause.Success);
        }
        finally
        {
            LogElapsed(nameof(LoadNextTrackFirstFrameForGapFreeze), started);
        }
    }

    public void LoadPreviousTrackFinalFrameForGapFreeze(PlaylistTrack previousTrack, double target, double duration, double fps)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            _effects.ResetEndAdvanceTriggered();
            if (!_effects.IsPlayerReady())
                return;

            _gapFreezeHandler.RecordPauseOwnership(_effects.IsPlaybackPaused?.Invoke() ?? false);

            // D32: 別の目標へ入り直すときは、前のフリーズ画像を破棄してからロードする。
            DiscardFrozenFrameIfTargetChanged(previousTrack.Id, target, fps);

            GapLoadCommandResult commandResult = _effects.LoadPausedAt(previousTrack.FilePath, target);

            if (!commandResult.Load.Success)
            {
                Log.Warning(
                    "Continue mode: gap freeze previous-track load failed track={Track} target={Target:F3} error={Error} pauseOk={PauseOk}",
                    previousTrack.Name, target, commandResult.Load.Error, commandResult.Pause.Success);
                _gapFreezeHandler.ForceFreezeComplete();
                return;
            }

            // v0.5.3 段 3g: 読み込みが成功した後、同期側の口（ロード中の印を立てない）を通す。
            _effects.BeginGapFreezeLoad?.Invoke("load-paused-at");

            _effects.SetLoadedTrackId(previousTrack.Id);

            _effects.ApplyPauseState(true);
            _effects.ResetPlayerStateForNewTrack();
            _effects.SetDuration(duration);
            _effects.SetFps(fps);
            _gapFreezeHandler.EnterFreezeCaptureWithReload(previousTrack.Id, target, previousTrack.FilePath);

            // D21-b (c): ロードは開始位置つきでもシーク完了フレームを保証しない。
            // 一時停止のまま最終フレームへシークし直し、そのフレームの到着で確定する。
            bool seekSuccess = _effects.SeekTo(target);
            if (!seekSuccess)
            {
                Log.Warning(
                    "Continue mode: gap freeze previous-track seek failed track={Track} target={Target:F3}",
                    previousTrack.Name, target);
                _gapFreezeHandler.ForceFreezeComplete();
                return;
            }

            Log.Information(
                "Continue mode: loading previous track final frame for gap freeze track={Track} target={Target:F3} duration={Duration:F3} fps={Fps:F3} loadOk={LoadOk} pauseOk={PauseOk}",
                previousTrack.Name, target, duration, fps, commandResult.Load.Success, commandResult.Pause.Success);
        }
        finally
        {
            LogElapsed(nameof(LoadPreviousTrackFinalFrameForGapFreeze), started);
        }
    }

    public void HandleNoTracks()
    {
        long started = Stopwatch.GetTimestamp();
        try
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
        finally
        {
            LogElapsed(nameof(HandleNoTracks), started);
        }
    }

    private static void LogElapsed(string action, long started) =>
        Log.Debug("GapEnter {Action}: elapsedMs={ElapsedMs:F1}",
            action, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    private void ApplyGapPause()
    {
        _gapFreezeHandler.RecordPauseOwnership(_effects.IsPlaybackPaused?.Invoke() ?? false);
        // C1(a) 計測用: compose-black ではプレイヤーを止めない（黒は合成側が出す）。
        // 既定の pause は現状どおり PauseForGap を呼ぶ。
        if (_playerMode == GapPlayerMode.Pause)
            _effects.PauseForGap();
        if (OutputTrace.Current.IsEnabled)
            OutputTrace.Current.Record(new("gap.enter", "GAP", Stopwatch.GetTimestamp(),
                Detail: "mode=" + GapPlayerModePolicy.Describe(_playerMode)));
        _effects.ApplyPauseState(true);
    }

    /// <summary>
    /// D32: 進入目標が今のフリーズ画像の目標（確定済み = Cached、捕捉中 = Pending）と変わるとき
    /// だけ、前のフリーズ画像とキャッシュを破棄する。同じ目標の再進入では破棄しない
    /// （F-1 の周期再進入で frozen を捨てない）。
    /// </summary>
    private void DiscardFrozenFrameIfTargetChanged(Guid? trackId, double targetSeconds, double fps)
    {
        double frameSeconds = fps > 0 ? 1.0 / fps : 1.0 / GapFreezeHandler.DefaultFallbackFps;
        if (!_gapFreezeHandler.ShouldDiscardFrozenFrame(trackId, targetSeconds, frameSeconds))
            return;

        _gapFreezeHandler.ClearCachedFrameInfo();
        _effects.ClearGapFreezeFrame();
        Log.Information(
            "Continue mode: gap freeze target changed, discarding the previous frozen frame track={TrackId} target={Target:F3}",
            trackId, targetSeconds);
    }
}

/// <summary>
/// <see cref="GapEnterCoordinator"/> が使用する副作用デリゲート群。
/// MainWindow のフィールド・メソッド（player を閉じ込めた <see cref="GapPlaybackCommandExecutor"/> 呼び出し・
/// フリーズ画像の世代クリア・状態フィールド更新）をフェイク可能な形で注入する。
/// _endAdvanceTriggered / _loadedTrackId / _duration / _fps の更新は現行タイミングを保つため
/// デリゲート経由で行う。GapFreezeHandler の状態遷移は具象クラスへ直接委譲する。
/// </summary>
internal sealed record GapEnterEffects(
    Action ResetEndAdvanceTriggered,
    Action PauseForGap,
    Action<bool> ApplyPauseState,
    Action ClearGapFreezeFrame,
    Func<double, bool> SeekTo,
    Func<(int rc, double duration)> GetPlayerDuration,
    Func<bool> IsPlayerReady,
    Func<string, double, GapLoadCommandResult> LoadPausedAt,
    Action ResetPlayerStateForNewTrack,
    Func<Guid?> GetLoadedTrackId,
    Action<Guid> SetLoadedTrackId,
    Func<double> GetDuration,
    Action<double> SetDuration,
    Func<double> GetFps,
    Action<double> SetFps,
    Func<GapBehavior> GetGapBehavior,
    Action UpdateCurrentTrackLabel,
    Func<bool>? IsPlaybackPaused = null,
    // v0.5.3 段 3g: ギャップの読み込みが成功した後に通す同期側の口（ロード中の印を立てない）。
    Action<string>? BeginGapFreezeLoad = null);
