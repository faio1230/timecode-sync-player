using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests.LatchLifetime;

/// <summary>
/// v0.5.2 段 0: ラッチを立てる手順と、できごとを起こす手順をそれぞれ 1 か所に置く。
/// できごとは MainWindow が呼んでいるのと同じ公開／internal メソッドで起こす（下の Fire の各分岐に、
/// MainWindow 側の呼び出し元を書く）。ラッチの読み取りは各クラスの LatchSnapshot だけを使う
/// （フィールドをリフレクションで読まない）。
/// </summary>
internal sealed class LatchLifetimeScenario
{
    /// <summary>Continue の配置: [0,30) と [40,70) の 2 トラック。30〜40 がギャップ。</summary>
    private const double GapSeconds = 35.0;
    private const double FrameSeconds = 0.04;

    private LatchLifetimeScenario(SyncScenarioHarness harness, ManualTimeProvider clock, double lastLtc)
    {
        Harness = harness;
        Clock = clock;
        LastLtc = lastLtc;
    }

    public SyncScenarioHarness Harness { get; }
    public ManualTimeProvider Clock { get; }

    /// <summary>最後に供給した LTC（秒）。NormalFrame などはここから 1 フレーム進める。</summary>
    public double LastLtc { get; private set; }

    public SyncMode Mode => Harness.Mode;

    public bool Read(LatchId latch)
    {
        IReadOnlyDictionary<string, bool> snapshot = latch.Owner switch
        {
            LatchOwner.LtcSyncController => Harness.Controller.LatchSnapshot(),
            LatchOwner.TimecodeSyncService => Harness.SyncService.LatchSnapshot(),
            LatchOwner.SingleModeSyncCoordinator => Harness.Single.LatchSnapshot(),
            LatchOwner.LtcSignalLossPolicy => Harness.Controller.SignalLossLatchSnapshot(),
            LatchOwner.TimecodeSyncSeekState =>
                ((TimecodeSyncSeekState)Harness.SeekState).LatchSnapshot(Clock.GetUtcNow().UtcDateTime),
            _ => throw new ArgumentOutOfRangeException(nameof(latch)),
        };
        return snapshot.TryGetValue(latch.Name, out bool value)
            ? value
            : throw new KeyNotFoundException($"{latch.Owner} の LatchSnapshot に {latch.Name} が無い");
    }

    // ─── 供給 ─────────────────────────────────────────────────────────

    public void Frame(double seconds, TimecodeFrameDiagnosticStatus status = TimecodeFrameDiagnosticStatus.Normal)
    {
        Harness.Controller.ReceiveProcessedFrame(new LtcFrameProcessingResult(
            "scenario", $"{seconds:F3} s", seconds, 25, "fps: 25",
            new TimecodeFrameDiagnosticResult(status, 0, 0),
            ShouldApplySync: status is TimecodeFrameDiagnosticStatus.Normal or TimecodeFrameDiagnosticStatus.Initial,
            ShouldLogFps: false), 10_000);
        if (status != TimecodeFrameDiagnosticStatus.Jump)
            LastLtc = seconds;
    }

    /// <summary>再生と LTC を 1 フレームずつ進めた通常フレーム（時計も 40ms 進める）。</summary>
    public void NextNormalFrame()
    {
        Clock.Advance(TimeSpan.FromMilliseconds(40));
        Harness.AdvancePlayback(Harness.PlaybackSeconds + FrameSeconds, 1);
        Frame(LastLtc + FrameSeconds);
    }

    /// <summary>同値の保持（Duplicate）を送りつつ 100ms ずつ 3 回（保持が理由の損失にする）。</summary>
    public void HeldPastTimeout()
    {
        for (int i = 0; i < 3; i++)
        {
            Frame(LastLtc, TimecodeFrameDiagnosticStatus.Duplicate);
            Harness.Tick100Milliseconds();
        }
    }

    /// <summary>無音のまま 100ms ずつ 3 回（timeout 250ms を超える）。</summary>
    public void SilencePastTimeout()
    {
        for (int i = 0; i < 3; i++)
            Harness.Tick100Milliseconds();
    }

    // ─── 配置 ─────────────────────────────────────────────────────────

    /// <summary>
    /// Continue の基本配置: 2 トラック、LTC 12.0 で 1 本目を読み込み、ロードの解除まで進める。
    /// 解除の回収待ち（fileLoadReleasePending）は 2 秒進めて鮮度切れにしておく。
    /// </summary>
    public static LatchLifetimeScenario Continue(LtcSignalLossMode lossMode = LtcSignalLossMode.RunThrough)
    {
        LatchLifetimeScenario s = Create(lossMode);
        SyncScenarioHarness h = s.Harness;
        h.AddTrack("first", 0, 30);
        h.AddTrack("second", 40, 30);
        h.ManualPlay();
        s.Frame(12.0);                    // 1 本目へ切替（ロード開始）
        s.Clock.Advance(TimeSpan.FromMilliseconds(200));
        h.AdvancePlayback(12.2, 3);
        s.Frame(12.2);                    // 再生と描画が進んだのでロード解除
        s.Clock.Advance(TimeSpan.FromSeconds(2));
        h.AdvancePlayback(12.24, 1);
        s.Frame(12.24);
        return s;
    }

    /// <summary>
    /// Single の基本配置: 1 トラックを一時停止で読み込み（読み込み済みトラックを持つ）、尺 20 秒、
    /// 再生 12.0・LTC 12.0。読み込みのゲートは最初のフレームで解除させ、回収待ちは 2 秒進めて
    /// 鮮度切れにしておく。
    /// </summary>
    public static LatchLifetimeScenario Single(LtcSignalLossMode lossMode = LtcSignalLossMode.RunThrough)
    {
        LatchLifetimeScenario s = Create(lossMode);
        SyncScenarioHarness h = s.Harness;
        h.AddTrack("first", 0, 30);
        h.ReloadProject();                // 一時停止の読み込み（MainWindow は LoadFilePaused）
        h.BeginManualFileLoad();          // LoadFilePaused → BeginSyncFileLoad(0)
        h.SetDurationSeconds(20);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        h.AdvancePlayback(12.0, 5);
        s.Clock.Advance(TimeSpan.FromMilliseconds(200));
        s.Frame(12.0);                    // 再生と描画が進んだのでロード解除
        s.Clock.Advance(TimeSpan.FromSeconds(2));
        h.AdvancePlayback(12.04, 1);
        s.Frame(12.04);
        return s;
    }

    private static LatchLifetimeScenario Create(LtcSignalLossMode lossMode)
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true, sampleClockEnabled: false)
        {
            SignalLossMode = lossMode,
        };
        return new LatchLifetimeScenario(h, clock, 0.0);
    }

    // ─── できごと ───────────────────────────────────────────────────────

    /// <summary>
    /// できごとの前提（その状態でしか起きないもの）を作り、前提ができたかを返す。ラッチはこの後も
    /// 立ったままのはず（消えたら、または前提ができなければ、その行は NotApplicable）。
    /// </summary>
    public bool Prepare(LifecycleEvent evt)
    {
        switch (evt)
        {
            case LifecycleEvent.SyncEnabledOn:
                Harness.SetSyncEnabled(false);
                return !Harness.SyncEnabled;
            case LifecycleEvent.MonitoringStarted:
                StopMonitoring();
                return !Harness.IsMonitoring;
            case LifecycleEvent.GapExit:
                Frame(GapSeconds);
                return Harness.IsGapActive;
            case LifecycleEvent.SignalRecovered:
                SilencePastTimeout();
                return IsSignalLost;
            case LifecycleEvent.JumpRecovery:
                HeldPastTimeout();
                return IsSignalLost;
            default:
                return true;
        }
    }

    private bool IsSignalLost => Harness.Controller.SignalLossLatchSnapshot()["lost"];

    public void Fire(LifecycleEvent evt)
    {
        SyncScenarioHarness h = Harness;
        switch (evt)
        {
            case LifecycleEvent.FileLoad:
                // MainWindow: PlaybackOperationsCoordinator.LoadFile（位置なし）/ LoadFilePaused →
                // BeginSyncFileLoad → TimecodeSyncService.BeginFileLoad（MainWindow.xaml.cs:1666）。
                h.BeginManualFileLoad();
                break;
            case LifecycleEvent.FileLoadWithoutBegin:
                // ギャップの 2 経路（LoadPausedAt / GapFreezePathGuard）の読み込み。読み込みの後に
                // ロード中の印を立てない口（BeginGapFreezeLoad、source load-paused-at）を通る。
                h.LoadCurrentFile();
                break;
            case LifecycleEvent.SyncModeChanged:
                // MainWindow.xaml.cs:446 → LtcSyncController.SyncModeChanged。
                h.ChangeMode(h.Mode == SyncMode.Single ? SyncMode.Continue : SyncMode.Single);
                break;
            case LifecycleEvent.SyncEnabledOff:
                // MainWindow.xaml.cs:433 → LtcSyncController.SyncEnabledChanged。
                h.SetSyncEnabled(false);
                break;
            case LifecycleEvent.SyncEnabledOn:
                h.SetSyncEnabled(true);
                break;
            case LifecycleEvent.MonitoringStopped:
                StopMonitoring();
                break;
            case LifecycleEvent.MonitoringStarted:
                // SyncViewModel が IsLtcRunning = true → MainWindow.xaml.cs:492 → MonitoringChanged。
                h.IsMonitoring = true;
                break;
            case LifecycleEvent.ManualSeek:
                // シークバー: MouseDown（MainWindow.xaml.cs:1977）と確定（:2642）で CancelPendingSync。
                h.BeginSeekBarInteraction();
                h.EndSeekBarInteraction(h.PlaybackSeconds);
                break;
            case LifecycleEvent.StopPlayback:
                // MainWindow.StopPlayback（MainWindow.xaml.cs:1536）→ PlaybackStopped（段 0 では CorrectionReset）。
                h.Controller.PlaybackStopped();
                h.StopPlayback();
                break;
            case LifecycleEvent.GapEnter:
                Frame(GapSeconds);
                break;
            case LifecycleEvent.GapExit:
                // 同じトラックへ戻る（次のトラックへ出ると読み込みが混ざるため）。
                Frame(29.0);
                break;
            case LifecycleEvent.SignalRecovered:
                // 有効フレーム 3 枚（resumeFrames=3）。
                for (int i = 0; i < 3; i++)
                    NextNormalFrame();
                break;
            case LifecycleEvent.JumpRecovery:
                // 保持が理由の損失中に、値が動いた Jump 1 枚で即復帰する（LtcSyncController.cs:527-532）。
                Frame(LastLtc + 3.0, TimecodeFrameDiagnosticStatus.Jump);
                break;
            case LifecycleEvent.NormalFrame:
                NextNormalFrame();
                break;
            case LifecycleEvent.FpsModeChanged:
                // MainWindow.xaml.cs:467 → LtcSyncController.FpsModeChanged。
                h.Controller.FpsModeChanged();
                break;
            case LifecycleEvent.CorrectionModeChanged:
                // MainWindow.xaml.cs:470-476: 設定の保存とログだけ。段 1 から同期側の入口
                // （CorrectionModeChanged）を呼ぶが、どのラッチも消さない。
                h.CorrectionMode = h.CorrectionMode == SyncCorrectionMode.Smooth
                    ? SyncCorrectionMode.Jump : SyncCorrectionMode.Smooth;
                h.Controller.CorrectionModeChanged();
                break;
            case LifecycleEvent.SignalLossModeChanged:
                // MainWindow.xaml.cs:484-490: 設定の保存とログだけ。段 1 から同期側の入口
                // （SignalLossModeChanged）を呼ぶが、どのラッチも消さない。
                h.SignalLossMode = h.SignalLossMode == LtcSignalLossMode.Stop
                    ? LtcSignalLossMode.RunThrough : LtcSignalLossMode.Stop;
                h.Controller.SignalLossModeChanged();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(evt));
        }
    }

    /// <summary>
    /// 利用者の停止: SyncViewModel が _ltcMonitor.Stop() の後に IsLtcRunning = false
    /// （MainWindow.xaml.cs:492 → MonitoringChanged）、続いてモニターの Stopped が
    /// MainWindow.xaml.cs:1157 → MonitorStopped(null) に届く。
    /// </summary>
    private void StopMonitoring()
    {
        Harness.IsMonitoring = false;
        Harness.Controller.MonitorStopped(null);
    }
}
