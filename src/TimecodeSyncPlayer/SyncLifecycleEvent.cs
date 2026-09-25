namespace TimecodeSyncPlayer;

/// <summary>
/// v0.5.2 段 1: 同期のラッチ（一時状態）を消すきっかけになるできごと。
/// 外部の操作は、既存の入口で <see cref="SyncLifecycle.Record"/> の後に各クラスの
/// OnLifecycle(evt) へ渡し、そこで今と同じラッチを消す（振る舞いは変えない）。
/// フレームの中で起きる遷移（ギャップの出入り・信号の復帰）は、遷移点でログだけを出す
/// （消す処理はフレーム経路のまま。段 2 で状態の型へ移す）。
/// 出す場所と条件は、段 0 の寿命の表の「現状」の列に合わせる。BeginFileLoad を通らない
/// 読み込み（GPU 復旧・自動送り・ギャップの LoadPausedAt）からは FileLoad を出さない
/// （出すと今は消えていないラッチが消える。設計書 §6 の 2、v0.5.3）。
/// </summary>
internal enum SyncLifecycleEvent
{
    /// <summary>BeginFileLoad を通る読み込み（手動・一時停止の読み込み・Continue のトラック切替）。</summary>
    FileLoad,
    /// <summary>
    /// v0.5.3 段 3g: ギャップの読み込み（Freeze の取り込みと読み直し）。ロード中の印を立てない口
    /// （<see cref="TimecodeSyncService.BeginGapFreezeLoad"/>）を通る。source は load-paused-at / path-guard。
    /// </summary>
    GapFreezeLoad,
    SyncModeChanged,
    SyncEnabled,
    SyncDisabled,
    /// <summary>監視の開始（SyncViewModel.IsLtcRunning が true になった）。</summary>
    MonitoringStarted,
    /// <summary>監視の停止（SyncViewModel.IsLtcRunning が false になった）。</summary>
    MonitoringStopped,
    /// <summary>モニターの停止通知（エラーによる停止を含む）。MonitoringStopped と消し方が違う。</summary>
    MonitorDeviceStopped,
    /// <summary>シークバー・相対シーク（CancelPendingSync）。</summary>
    ManualSeek,
    /// <summary>タイムラインのクリック（手動シークに加えてシークの保留状態も捨てる）。</summary>
    TimelineSeek,
    /// <summary>再生の停止（プロジェクト・プレイリストの差し替えを含む）。</summary>
    PlaybackStopped,
    /// <summary>操作者の再生・一時停止。</summary>
    PlayPauseToggled,
    /// <summary>ギャップの状態が Inactive から外れた（ログだけ）。</summary>
    GapEnter,
    /// <summary>ギャップの状態が Inactive に戻った（ログだけ）。</summary>
    GapExit,
    /// <summary>信号断からの復帰（有効フレーム N 枚、または保持の直後の Jump。ログだけ）。</summary>
    SignalRecovered,
    /// <summary>Single の境界ホールドの解除（保持着地・保持値の 1 回適用・同期の保留を消す）。</summary>
    BoundaryHoldReleased,
    FpsModeChanged,
    CorrectionModeChanged,
    SignalLossModeChanged,
}

internal static class SyncLifecycle
{
    /// <summary>
    /// できごとを 1 行ログに残す。候補どうしで同じシナリオのできごとの列を突き合わせるため、
    /// 文言は "Sync lifecycle: {Event} source={Source}" で固定する（解析スクリプトが読む）。
    /// </summary>
    public static void Record(SyncLifecycleEvent evt, string source)
        => Serilog.Log.Information("Sync lifecycle: {Event} source={Source}", evt, source);
}
