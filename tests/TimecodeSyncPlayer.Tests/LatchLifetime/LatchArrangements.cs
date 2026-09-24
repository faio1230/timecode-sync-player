using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests.LatchLifetime;

/// <summary>
/// v0.5.2 段 0: ラッチごとの「立てる手順」。基本配置（Continue / Single）の上で、今のコードの
/// 経路を通してラッチを立てる（フィールドを直接書かない）。
/// </summary>
internal static class LatchArrangements
{
    public static readonly LatchId JumpAppliedOnce = new(LatchOwner.LtcSyncController, "jumpAppliedOnce");
    public static readonly LatchId HeldReapplyDone = new(LatchOwner.LtcSyncController, "heldReapplyDone");
    public static readonly LatchId PendingJump = new(LatchOwner.LtcSyncController, "pendingJump");
    public static readonly LatchId PendingSync = new(LatchOwner.LtcSyncController, "pendingSync");
    public static readonly LatchId LastHeldEffective = new(LatchOwner.LtcSyncController, "lastHeldEffective");
    public static readonly LatchId HeldLossLanding = new(LatchOwner.LtcSyncController, "heldLossLanding");
    public static readonly LatchId LastAppliedLtc = new(LatchOwner.LtcSyncController, "lastAppliedLtc");
    public static readonly LatchId LastAcceptedLtc = new(LatchOwner.LtcSyncController, "lastAcceptedLtc");
    public static readonly LatchId FollowStartPending = new(LatchOwner.LtcSyncController, "followStartPending");
    public static readonly LatchId RateRestorePending = new(LatchOwner.LtcSyncController, "rateRestorePending");
    public static readonly LatchId SmoothUnavailable = new(LatchOwner.LtcSyncController, "smoothUnavailable");
    public static readonly LatchId CorrectionPausedForPosition =
        new(LatchOwner.LtcSyncController, "correctionPausedForPosition");
    public static readonly LatchId RateNotUnity = new(LatchOwner.LtcSyncController, "rateNotUnity");

    public static readonly LatchId LoadingFile = new(LatchOwner.TimecodeSyncService, "loadingFile");
    public static readonly LatchId FileLoadReleasePending = new(LatchOwner.TimecodeSyncService, "fileLoadReleasePending");
    public static readonly LatchId SeekLandingActive = new(LatchOwner.TimecodeSyncService, "seekLandingActive");
    public static readonly LatchId FollowStartLanding = new(LatchOwner.TimecodeSyncService, "followStartLanding");
    public static readonly LatchId PositionUntrusted = new(LatchOwner.TimecodeSyncService, "positionUntrusted");

    public static readonly LatchId ClipBoundaryHeld = new(LatchOwner.SingleModeSyncCoordinator, "clipBoundaryHeld");
    public static readonly LatchId BoundarySeekTarget = new(LatchOwner.SingleModeSyncCoordinator, "boundarySeekTarget");

    public static readonly LatchId Lost = new(LatchOwner.LtcSignalLossPolicy, "lost");
    public static readonly LatchId PausedByPolicy = new(LatchOwner.LtcSignalLossPolicy, "pausedByPolicy");
    public static readonly LatchId ManualResumeSuppressesPause =
        new(LatchOwner.LtcSignalLossPolicy, "manualResumeSuppressesPause");

    public static readonly LatchId PendingSeek = new(LatchOwner.TimecodeSyncSeekState, "pendingSeek");
    public static readonly LatchId LastSettledRecent = new(LatchOwner.TimecodeSyncSeekState, "lastSettledRecent");

    public static IReadOnlyList<LatchId> All { get; } =
    [
        JumpAppliedOnce, HeldReapplyDone, PendingJump, PendingSync, LastHeldEffective, HeldLossLanding,
        LastAppliedLtc, LastAcceptedLtc, FollowStartPending, RateRestorePending, SmoothUnavailable,
        CorrectionPausedForPosition, RateNotUnity,
        LoadingFile, FileLoadReleasePending, SeekLandingActive, FollowStartLanding, PositionUntrusted,
        ClipBoundaryHeld, BoundarySeekTarget,
        Lost, PausedByPolicy, ManualResumeSuppressesPause,
        PendingSeek, LastSettledRecent,
    ];

    /// <summary>
    /// ラッチを立てる配置のモード。ギャップの出入りは Continue にしか無いので、Single でしか
    /// 立たないラッチ（境界ホールド）のギャップの行は NotApplicable になる。
    /// </summary>
    public static SyncMode ModeFor(LatchId latch, LifecycleEvent evt)
    {
        if (latch.Owner == LatchOwner.SingleModeSyncCoordinator)
            return SyncMode.Single;
        return evt is LifecycleEvent.GapEnter or LifecycleEvent.GapExit ? SyncMode.Continue : SyncMode.Single;
    }

    public static LatchLifetimeScenario Arrange(LatchId latch, SyncMode mode)
    {
        LtcSignalLossMode lossMode =
            latch == HeldLossLanding || latch == PausedByPolicy || latch == ManualResumeSuppressesPause
                ? LtcSignalLossMode.Stop
                : LtcSignalLossMode.RunThrough;
        LatchLifetimeScenario s = mode == SyncMode.Continue
            ? LatchLifetimeScenario.Continue(lossMode)
            : LatchLifetimeScenario.Single(lossMode);
        SyncScenarioHarness h = s.Harness;

        if (latch == JumpAppliedOnce)
        {
            // 同じトラック内の Jump は次の 1 フレームで確かめてから適用する（LtcSyncController.cs:685）。
            // 確認フレームで ApplyConfirmedJump が立てる（:643）。
            double jump = s.LastLtc + 3.0;
            s.Frame(jump, TimecodeFrameDiagnosticStatus.Jump);
            s.Frame(jump + 0.04);
        }
        else if (latch == HeldReapplyDone)
        {
            // 最後の適用値から許容を超えて離れた保持値（LtcSyncController.cs:566-571）。
            s.Frame(s.LastLtc + 3.0, TimecodeFrameDiagnosticStatus.Duplicate);
        }
        else if (latch == PendingJump)
        {
            // 未確認の Jump（LtcSyncController.cs:513）。
            s.Frame(s.LastLtc + 3.0, TimecodeFrameDiagnosticStatus.Jump);
        }
        else if (latch == PendingSync)
        {
            // ネイティブシーク中は同期要求が Deferred になり、再送用に保持する（:317-322）。
            h.NativeSeeking = true;
            s.NextNormalFrame();
            h.NativeSeeking = false;
        }
        else if (latch == LastHeldEffective)
        {
            // 同値の保持（Duplicate）。着地目標として保持値を覚える（:499）。
            s.Frame(s.LastLtc, TimecodeFrameDiagnosticStatus.Duplicate);
        }
        else if (latch == HeldLossLanding)
        {
            // 停止モードで保持が timeout を超え、一時停止して保持値へ着地する（:1079）。
            s.HeldPastTimeout();
        }
        else if (latch == LastAppliedLtc || latch == LastAcceptedLtc)
        {
            // 基本配置の通常フレームで立つ（:597, :600）。
        }
        else if (latch == FollowStartPending)
        {
            // 監視の開始（同期が有効）で立つ（:388-396）。
            h.IsMonitoring = true;
        }
        else if (latch == RateRestorePending)
        {
            // Smooth の倍率が残ったまま、倍率を 1.0 に戻せない状態で補正状態を捨てる（:292-297）。
            ApplySmoothRate(s);
            h.RateApplySucceeds = false;
            h.Controller.CorrectionReset();
            h.RateApplySucceeds = true;
        }
        else if (latch == RateNotUnity)
        {
            // Smooth の倍率を掛けたまま（:864-872）。
            ApplySmoothRate(s);
        }
        else if (latch == SmoothUnavailable)
        {
            // レート変更が拒否される（:865-868）。
            h.RateApplySucceeds = false;
            ApplySmoothRate(s);
            h.RateApplySucceeds = true;
        }
        else if (latch == CorrectionPausedForPosition)
        {
            // 位置が不安定な間は補正を止める（:797-801）。条件はその後に解消させる。
            h.PlaybackPositionUnstable = true;
            s.NextNormalFrame();
            h.PlaybackPositionUnstable = false;
        }
        else if (latch == LoadingFile)
        {
            h.BeginManualFileLoad();
        }
        else if (latch == FileLoadReleasePending)
        {
            // 読み込みの後、再生と描画が進んだフレームで解除する（TimecodeSyncService.cs:481-485）。
            h.BeginManualFileLoad();
            s.Clock.Advance(TimeSpan.FromMilliseconds(200));
            h.AdvancePlayback(h.PlaybackSeconds + 0.2, 3);
            s.Frame(s.LastLtc + 0.2);
        }
        else if (latch == SeekLandingActive || latch == FollowStartLanding ||
                 latch == PendingSeek || latch == PositionUntrusted)
        {
            // 追従開始（監視の開始）から、離れた位置へ同期シークを出す。
            h.IsMonitoring = true;
            SeekFromFarPosition(s);
        }
        else if (latch == LastSettledRecent)
        {
            h.IsMonitoring = true;
            SeekFromFarPosition(s);
            // 着地（目標の近くで 200ms の冷却を過ぎる。TimecodeSyncSeekState.cs:78-96）。
            for (int i = 0; i < 8 && h.SeekState.HasPendingSeek; i++)
                s.NextNormalFrame();
        }
        else if (latch == ClipBoundaryHeld || latch == BoundarySeekTarget)
        {
            // 範囲外の LTC で端（clipOut = 尺 20）へシークし（NoteBoundarySeek）、着いたらホールド。
            s.Frame(25.0);
            for (int i = 0; i < 8 && !s.Read(latch); i++)
            {
                s.Clock.Advance(TimeSpan.FromMilliseconds(40));
                s.Frame(s.LastLtc + 0.04);
            }
        }
        else if (latch == Lost || latch == PausedByPolicy)
        {
            s.SilencePastTimeout();
        }
        else if (latch == ManualResumeSuppressesPause)
        {
            // 信号断で止めた後に利用者が再生する（LtcSignalLossPolicy.cs:259-270 の ObservePlaybackState が :265-266 で立てる）。
            s.SilencePastTimeout();
            h.ManualPlay();
            h.Tick100Milliseconds();
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(latch), latch, null);
        }

        return s;
    }

    /// <summary>再生を LTC より 0.2 秒先行させた通常フレームで、Smooth の倍率を掛けさせる。</summary>
    private static void ApplySmoothRate(LatchLifetimeScenario s)
    {
        SyncScenarioHarness h = s.Harness;
        for (int i = 0; i < 4; i++)
        {
            s.Clock.Advance(TimeSpan.FromMilliseconds(40));
            h.AdvancePlayback(s.LastLtc + 0.04 + 0.2, 1);
            s.Frame(s.LastLtc + 0.04);
        }
    }

    /// <summary>再生を 7 秒手前に置いて通常フレームを送り、同期シークを出させる。</summary>
    private static void SeekFromFarPosition(LatchLifetimeScenario s)
    {
        SyncScenarioHarness h = s.Harness;
        h.AdvancePlayback(s.LastLtc - 7.0, 1);
        for (int i = 0; i < 8 && !h.SeekState.HasPendingSeek; i++)
        {
            s.Clock.Advance(TimeSpan.FromMilliseconds(40));
            h.AdvancePlayback(h.PlaybackSeconds + 0.04, 1);
            s.Frame(s.LastLtc + 0.04);
        }
    }
}
