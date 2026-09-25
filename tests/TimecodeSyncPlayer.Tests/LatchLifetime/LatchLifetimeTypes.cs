namespace TimecodeSyncPlayer.Tests.LatchLifetime;

/// <summary>ラッチを持つクラス（LatchSnapshot を持つ 5 つ）。</summary>
public enum LatchOwner
{
    LtcSyncController,
    TimecodeSyncService,
    SingleModeSyncCoordinator,
    LtcSignalLossPolicy,
    TimecodeSyncSeekState,
}

/// <summary>
/// ラッチを消すきっかけになり得るできごと（設計書 §3 段 1 の一覧と、段 0 の指示の一覧）。
/// </summary>
public enum LifecycleEvent
{
    /// <summary>BeginFileLoad を通る読み込み（手動の次／前／プレイリスト、一時停止の読み込み、Continue の切替）。</summary>
    FileLoad,
    /// <summary>BeginFileLoad を通らない位置つきの読み込み（GPU 復旧・自動送り・ギャップの LoadPausedAt）。</summary>
    FileLoadWithoutBegin,
    SyncModeChanged,
    SyncEnabledOff,
    SyncEnabledOn,
    MonitoringStopped,
    MonitoringStarted,
    /// <summary>手動シーク（シークバー。CancelPendingSync）。</summary>
    ManualSeek,
    /// <summary>再生の停止（CorrectionReset）。</summary>
    StopPlayback,
    GapEnter,
    GapExit,
    /// <summary>無音の損失から有効フレーム N 枚で復帰する。</summary>
    SignalRecovered,
    /// <summary>保持が理由の損失中に、値が動いた Jump 1 枚で即復帰する（設計書 §6 の 4）。</summary>
    JumpRecovery,
    NormalFrame,
    FpsModeChanged,
    CorrectionModeChanged,
    SignalLossModeChanged,
}

/// <summary>今のコードでの振る舞い（できごとの直後にラッチが立っているか）。</summary>
public enum CurrentBehavior
{
    /// <summary>できごとの直後にラッチが下りている。</summary>
    Clears,
    /// <summary>できごとの直後もラッチが立っている（消えて同じできごとの中で立て直された場合を含む。根拠に書く）。</summary>
    Keeps,
    /// <summary>ラッチを立てた状態でこのできごとを起こせない（理由を根拠に書く）。テストしない。</summary>
    NotApplicable,
}

/// <summary>意図（設計書 §6 の候補に合わせる）。</summary>
public enum LatchIntent
{
    Clear,
    Keep,
    Undecided,
}

public sealed record LatchId(LatchOwner Owner, string Name)
{
    public override string ToString() => $"{Owner}.{Name}";
}

/// <summary>寿命の表の 1 行。</summary>
public sealed record LatchLifetimeRow(
    LatchId Latch,
    LifecycleEvent Event,
    CurrentBehavior Current,
    LatchIntent Intent,
    string Evidence)
{
    /// <summary>現状と意図が食い違う行（設計書 §6 の候補そのもの）。</summary>
    public bool IsDiscrepancy =>
        (Current == CurrentBehavior.Keeps && Intent == LatchIntent.Clear) ||
        (Current == CurrentBehavior.Clears && Intent == LatchIntent.Keep);

    public override string ToString() => $"{Latch} × {Event}";
}
