using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Serilog;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Gst;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TimecodeSyncPlayer.Output;

internal readonly record struct PreviewFrame(byte[] Pixels, int Width, int Height, Action Release);

internal sealed class OutputEngineSettings
{
    public const string TestCardEnvironmentVariable = "TIMECODE_SYNC_PLAYER_TEST_CARD";
    public const string SimulateDeviceLossEnvironmentVariable = "TIMECODE_SYNC_PLAYER_SIMULATE_DEVICE_LOSS";
    public int CanvasWidth { get; init; } = 1920;
    public int CanvasHeight { get; init; } = 1080;
    public double Fps { get; init; } = 60;
    public double PresentMarginMs { get; init; } = 3;
    public double ComposeLeadMs { get; init; } = 3;
    public double SendPhaseMs { get; init; } = 4;
    public long? AdapterLuid { get; init; }
    public string SenderName { get; init; } = SpoutDefaults.DefaultSenderName;
    public bool SpoutEnabled { get; init; }
    public bool TestCardEnabled { get; init; }
    public OutputTrace Trace { get; init; } = OutputTrace.Disabled;
    public Action<PreviewFrame>? PreviewFrameReady { get; init; }

    /// <summary>
    /// GPU worker。source.acquire が Ready になったときの (QPC, 世代, ソース sequence, フレーム位置秒)。
    /// D7-a の先行補償が「新しい世代の最初のフレーム」を識別するために使い、
    /// D21-b のギャップ Freeze 確定がフレーム位置（PTS）で目標フレームを確認するために使う。
    /// </summary>
    public Action<long, int, long, double>? SourceFrameReady { get; init; }

    /// <summary>試験フック: GPU worker が指定時刻（起動からの秒）に GpuDeviceLostException を投げる。</summary>
    public IReadOnlyList<double> SimulatedDeviceLossSeconds { get; init; } = Array.Empty<double>();

    /// <summary>復旧状態の UI 通知（GPU worker から呼ばれる。UI 側で Dispatcher へ投げる）。</summary>
    public Action<GpuOutputStatus>? GpuStatusChanged { get; init; }

    /// <summary>
    /// 共有リングを開き直せないとき、新しい合成デバイスポインタを渡して player 再生成・再ロードを依頼する。
    /// UI 側は非同期に処理し、完了後に AttachGStreamerSource を呼ぶ（GPU worker を待たせない）。
    /// </summary>
    public Action<IntPtr>? GStreamerRebindRequested { get; init; }

    public static bool TestCardRequested()
    {
        string? value = Environment.GetEnvironmentVariable(TestCardEnvironmentVariable);
        return value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>TIMECODE_SYNC_PLAYER_SIMULATE_DEVICE_LOSS=&lt;秒&gt;[,&lt;秒&gt;] を起動時に 1 回だけ解析する。</summary>
    public static double[] ParseSimulatedDeviceLossSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var result = new List<double>();
        foreach (string part in value.Split(','))
        {
            if (double.TryParse(part.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double seconds)
                && seconds >= 0 && double.IsFinite(seconds))
            {
                result.Add(seconds);
            }
        }
        return [.. result];
    }
}

/// <summary>
/// GPU 出力層の骨格（段階 1）。D3D11 デバイス／合成 pool／共有フェンス／全画面 swapchain／Spout worker／
/// プレビュー読み戻しを所有する。ソースは持たず、黒キャンバスまたはテストカードを 60Hz で合成する。
/// 試作 scripts/GpuOutputProbe の ProbeEngine を本体向けに再構成したもの。
/// </summary>
internal sealed class OutputEngine : IDisposable
{
    private readonly OutputEngineSettings settings;
    private readonly CancellationTokenSource stop = new();
    private readonly object commandGate = new();
    private readonly Queue<Action> pendingCommands = new();
    // 現在のキャンバス世代（合成 pool 3 枚 + 共有サーフェス）。GPU worker が所有する。
    private CanvasSettings canvas = CanvasSettings.Default;
    private CanvasGeneration current = null!;
    private readonly List<RetiredGeneration> retired = new();
    private readonly ScanoutTracker scanout = new(16);
    private readonly ScheduleOffset scheduleOffset = new();
    private readonly ComposeAlignGate align;
    private readonly TimelineOutputMailbox timelineInput = new();
    private ComposeLayer? layer;
    private ComposeLeadController? composeLead;
    private TimelineOutputState? lastTimelineState;
    private int sourceGeneration = -1;
    private GStreamerSource? gstSource;
    private long lastGstSequence = -1;
    private long lastGstFenceWaited = -1;
    private int lastGstGeneration = -1;
    // 配信トレース（問題 H）。shim の QPC イベントを events.jsonl へ写す。
    private IGstNativeApi? gstNative;
    private IntPtr gstPlayer;
    private long gstDeliveryDrainQpc;
    private readonly VblankNotReadyGate notReadyGate = new();
    private GstNative.TcsDeliveryEvent[]? gstDeliveryBuffer;

    // GStreamer shim へのデバイス Adopt 用（GPU worker が初期化時に確定する）。
    private readonly ManualResetEventSlim deviceReady = new(false);
    private IntPtr devicePointer;

    // GPU worker のみが触る状態。
    private GpuDevice? gpu;
    private ShaderPipeline? shaders;
    private SharedFence? sharedFence;
    private SwapchainTarget? target;
    private VblankDisplayGate? vblank;
    private VblankWaitTimer? gpuLoopTimer;
    private VblankWaitTimer? vblankTimer;
    private ID3D11Texture2D? previewTexture;
    private ID3D11RenderTargetView? previewTarget;
    private ID3D11Texture2D? previewStaging;
    // A1: 計測有効時のみ使う読み戻し用ステージング（既定経路では null のまま）。
    private ID3D11Texture2D? accuracyStaging;
    private int accuracyStagingWidth, accuracyStagingHeight;
    private TickSchedule? composeSchedule;
    private long originQpc;
    private long nextImageId;
    private long publishedFrameCount;
    private long lastPreviewQpc;
    private bool testCard;
    private bool spoutRunning;
    private bool displayEverAttached;
    private bool pendingDisplayTrace;
    private bool spoutEverEnabled;
    private bool? vblankTimerHighResolution;
    private bool gpuLoopTimerHighResolution;
    private bool? spoutLoopTimerHighResolution;
    private string? firstFault;
    private int faulted;
    private bool started;
    private bool disposed;
    private TaskCompletionSource? gpuDone;

    // デバイス消失復旧（段階 5.2）。
    private readonly GpuRecoveryState recovery = new();
    private readonly ComposeLeadSuspension leadSuspension;
    private readonly ManualResetEventSlim recoveryRetry = new(false);
    private readonly object recoveryGate = new();
    private bool manualRetryRequested;
    private long[] simulatedLossAtQpc = [];
    private bool[] simulatedLossFired = [];
    private IntPtr recoveryDisplayHwnd;

    // Spout worker（GPU worker が起動・停止を管理）。
    private Thread? spoutThread;
    private CancellationTokenSource? spoutStop;
    private ManualResetEventSlim? spoutReady;
    private long spoutCpuStartTicks;

    private readonly PreviewHandoff previewHandoff = new(3);

    // D5 決定再現用: 環境変数 TCS_TEST_FORCE_GAP_BLACK_ON_SWITCH=1 のときだけ、世代切替
    // からその世代の最初のフレーム取得までギャップを Black に固定する。
    // 未設定なら forceGapBlackOnSwitch=false で、切替ごとの分岐 1 回のみ（既定経路は不変）。
    internal const string ForceGapBlackOnSwitchEnvironmentVariable = "TCS_TEST_FORCE_GAP_BLACK_ON_SWITCH";
    private static readonly bool forceGapBlackOnSwitch =
        Environment.GetEnvironmentVariable(ForceGapBlackOnSwitchEnvironmentVariable) == "1";
    private bool anyFrameAcquired;
    private bool armedForceGapBlack;

    public OutputEngine(OutputEngineSettings settings)
    {
        this.settings = settings;
        testCard = settings.TestCardEnabled;
        spoutRunning = settings.SpoutEnabled;
        spoutEverEnabled = settings.SpoutEnabled;
        align = new ComposeAlignGate(settings.ComposeLeadMs, Stopwatch.Frequency);
        leadSuspension = new ComposeLeadSuspension(Stopwatch.GetTimestamp);
    }

    public bool Faulted => Volatile.Read(ref faulted) != 0;
    public string? FirstFault => firstFault;
    public GpuRecoveryPhase RecoveryPhase => recovery.Phase;

    /// <summary>UI スレッド。Failed からの手動再試行（BtnGpuRetry）。</summary>
    public void RetryGpuRecovery()
    {
        if (disposed || recovery.Phase != GpuRecoveryPhase.Failed) return;
        lock (recoveryGate) manualRetryRequested = true;
        recoveryRetry.Set();
    }

    /// <summary>UI スレッド。タイムライン状態（ギャップ・カード・世代・位置）を GPU worker へ渡す。</summary>
    public void SubmitTimelineState(TimelineOutputState state)
        => timelineInput.Publish(state);

    /// <summary>GStreamer shim に Adopt させる ID3D11Device が確定するまで待つ（起動時のみ）。</summary>
    public bool WaitForDevice(TimeSpan timeout) => deviceReady.Wait(timeout);

    /// <summary>Adopt 済みデバイスポインタ（未初期化なら IntPtr.Zero）。</summary>
    public IntPtr DevicePointer => Volatile.Read(ref devicePointer);

    /// <summary>
    /// D4: 合成プール（表示経路）へ公開したフレーム数。CPU 合成の
    /// PlaybackPerformanceStats.TotalRenderedFrames に相当し、GPU 合成のロード安定ゲートが読む。
    /// </summary>
    internal long PublishedFrameCount => Interlocked.Read(ref publishedFrameCount);

    /// <summary>D8: リング外（旧サンプル経路）で拒否したフレーム数。2 秒ごとの統計に出す。</summary>
    internal long GstRingOutsideFrames => gstSource?.RingOutsideFrames ?? 0;

    /// <summary>
    /// UI スレッド。GStreamer プレイヤーをソースとして接続する（Gpu 出力時）。
    /// 以降、合成 tick は CPU アップロードではなく shim のリースを取得してエンジン slot へ GPU コピーする。
    /// </summary>
    public void AttachGStreamerSource(IntPtr player, TimecodeSyncPlayer.Gst.IGstNativeApi native, Action? onEnded = null)
        => Enqueue(() =>
        {
            if (player == IntPtr.Zero || gpu == null)
            {
                Fault("OutputEngine: GStreamer プレイヤーハンドルがありません");
                return;
            }
            // D26: Held は合成側が所有するキャンバスの複製で、ソースのリング面を参照しない。
            // ソースを差し替えても直前の絵を保持する（黒を挟まない）。
            gstSource?.Dispose();
            // ステージ 6b: shim は合成デバイスを Adopt せず、LUID だけを使って自前デバイスを作る。
            // 合成デバイスはリングを開いてフェンス待ちに使う（この gpu を渡す）。
            gstSource = new GStreamerSource(new GstNativeLeasePlayer(native, player),
                gpu.Luid.ToString(System.Globalization.CultureInfo.InvariantCulture), gpu,
                onRingOpened: () => leadSuspension.OnSharedRingOpened(),
                onEnded: onEnded);
            gstNative = native;
            gstPlayer = player;
            lastGstSequence = -1;
            lastGstFenceWaited = -1;
            lastGstGeneration = -1;
            // L-3: ソース接続直後は位相が乱れるため lead 学習を 1 秒除外する。
            leadSuspension.OnSourceAttached();
            settings.Trace.Add("lifecycle", "GPU", detail: "source.gstreamer");
            Log.Information("OutputEngine: GStreamerSource を接続しました");
        });

    public void Start()
    {
        if (started || disposed) return;
        started = true;
        gpuDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(Run)
        {
            Name = "OutputEngine.GPU",
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public void AttachFullscreen(IntPtr hwnd) => Enqueue(() =>
    {
        if (hwnd == IntPtr.Zero || gpu == null) return;
        DisposeDisplay();
        target = new SwapchainTarget(gpu, hwnd);
        vblank = new VblankDisplayGate(settings.PresentMarginMs, 0, Stopwatch.Frequency);
        vblankTimer = new VblankWaitTimer();
        vblankTimerHighResolution = vblankTimer.HighResolution;
        scanout.Reset(); // 接続し直した swapchain は PresentCount が 1 から始まる
        displayEverAttached = true;
        // L-2: 全画面接続直後は位相が乱れるため 1 秒間は lead 学習から除外する。
        composeLead?.SuspendLearning(Stopwatch.GetTimestamp());
        // 初期寸法は途中経過のことがあるため、最初の Present/Resize 時の最終寸法で記録する。
        pendingDisplayTrace = true;
        Log.Information("OutputEngine: 全画面 swapchain を接続 {W}x{H}", target.Width, target.Height);
    });

    /// <summary>子 HWND の最終寸法へ swapchain を追従させる。GPU worker が lease を持たないコマンド処理で行う。</summary>
    public void ResizeFullscreen(int width, int height) => Enqueue(() =>
    {
        if (target == null) return;
        int w = Math.Max(16, width), h = Math.Max(16, height);
        if (target.Width == w && target.Height == h) return;
        target.Resize(w, h);
        // リサイズ後は統計位相が途切れるため、vblank 予測を初期化する。
        vblank = new VblankDisplayGate(settings.PresentMarginMs, 0, Stopwatch.Frequency);
        if (pendingDisplayTrace)
        {
            pendingDisplayTrace = false;
            settings.Trace.Add("lifecycle", "GPU", detail: $"display.attach:{w}x{h}");
        }
        else
        {
            settings.Trace.Add("lifecycle", "GPU", detail: $"display.resize:{w}x{h}");
        }
        Log.Information("OutputEngine: 全画面 swapchain をリサイズ {W}x{H}", w, h);
    });

    /// <summary>子 HWND の破棄より先に swapchain を切断する。最大 500ms だけ GPU worker の完了を確認する。</summary>
    public void DetachFullscreen()
    {
        if (!started || disposed || stop.IsCancellationRequested) return;
        using var done = new ManualResetEventSlim(false);
        Enqueue(() =>
        {
            try
            {
                if (target == null) return;
                settings.Trace.Add("lifecycle", "GPU", detail: "display.detach");
                // L-2: 全画面切断直後は位相が乱れるため 1 秒間は lead 学習から除外する。
                composeLead?.SuspendLearning(Stopwatch.GetTimestamp());
                DisposeDisplay();
                Log.Information("OutputEngine: 全画面 swapchain を切断");
            }
            finally { done.Set(); }
        });
        if (!done.Wait(TimeSpan.FromMilliseconds(500)))
            Log.Warning("OutputEngine: 全画面切断の確認がタイムアウトしました");
    }

    public void SetTestCardEnabled(bool enabled) => Enqueue(() =>
    {
        if (testCard == enabled) return;
        testCard = enabled;
        settings.Trace.Add("lifecycle", "GPU", detail: "testCard:" + (enabled ? "on" : "off"));
    });

    /// <summary>
    /// UI スレッド。キャンバス寸法を変更する（段階 4.2）。GPU worker が合成 pool 3 枚と
    /// 共有サーフェスを新寸法で作り直し、次の合成から反映する。旧世代は lease が返るまで保持する。
    /// </summary>
    public void SetCanvas(CanvasSettings value) => Enqueue(() => ApplyCanvas(value));

    public void SetSpoutEnabled(bool enabled) => Enqueue(() =>
    {
        if (enabled == spoutRunning && (enabled || spoutThread == null)) return;
        if (enabled)
        {
            spoutRunning = true;
            spoutEverEnabled = true;
            if (spoutThread == null) StartSpoutWorker();
        }
        else
        {
            spoutRunning = false;
            StopSpoutWorker();
        }
        // L-2: 切替直後は合成位相が乱れるため 1 秒間は lead 学習から除外する。
        composeLead?.SuspendLearning(Stopwatch.GetTimestamp());
    });

    private void Enqueue(Action command)
    {
        if (disposed) return;
        lock (commandGate) pendingCommands.Enqueue(command);
    }

    private void ProcessCommands()
    {
        Action[] commands;
        lock (commandGate)
        {
            if (pendingCommands.Count == 0) return;
            commands = pendingCommands.ToArray();
            pendingCommands.Clear();
        }
        foreach (var command in commands)
        {
            try { command(); }
            catch (GpuDeviceLostException) { throw; } // 復旧経路へ（Fault で恒久停止させない）。
            catch (Exception e) { Fault("command: " + e); }
        }
    }

    private void Fault(string message)
    {
        Interlocked.CompareExchange(ref firstFault, message, null);
        if (Interlocked.Exchange(ref faulted, 1) == 0)
        {
            settings.Trace.Add("error", Thread.CurrentThread.Name ?? "worker", detail: message);
            Log.Error("OutputEngine: {Message}", message);
        }
        stop.Cancel();
    }

    // ── キャンバス変更（段階 4.2） ─────────────────────────────────

    // GPU worker。新しい世代の pool／サーフェスを作り、旧世代は lease が返るまで保持する。
    // Spout worker は共有ハンドルを開き直す必要があるため、旧世代と新世代を混ぜないよう先に停止する。
    private void ApplyCanvas(CanvasSettings value)
    {
        if (gpu == null || value == canvas) return;
        bool restartSpout = spoutRunning && spoutThread != null;
        if (restartSpout) StopSpoutWorker();
        var next = new CanvasGeneration(value);
        for (int i = 0; i < next.Pool.Capacity; i++)
            next.Surfaces.Add(new Surface(gpu, gpu.Texture(value.Width, value.Height, SourceSharing.FenceNt), true, SourceSharing.FenceNt));
        var entry = new RetiredGeneration(current);
        entry.Plan.Request();
        retired.Add(entry);
        current = next;
        canvas = value;
        layer!.SetCanvas(value);
        settings.Trace.Add("lifecycle", "GPU", detail: $"canvas:{value.Width}x{value.Height}");
        Log.Information("OutputEngine: canvas を {W}x{H} へ変更", value.Width, value.Height);
        if (restartSpout) StartSpoutWorker();
    }

    // 旧世代の画像は、新世代の最初の合成画像が公開され、かつ lease が全て返るまで表示に使う。
    private (LatestPool.Lease? Lease, CanvasGeneration? Generation) AcquireLatestImage()
    {
        var lease = current.Pool.AcquireLatest();
        if (lease != null) return (lease, current);
        for (int i = retired.Count - 1; i >= 0; i--)
        {
            lease = retired[i].Generation.Pool.AcquireLatest();
            if (lease != null) return (lease, retired[i].Generation);
        }
        return (null, null);
    }

    private long LatestVisibleId()
    {
        long id = current.Pool.LatestId;
        if (id != 0) return id;
        for (int i = retired.Count - 1; i >= 0; i--)
        {
            id = retired[i].Generation.Pool.LatestId;
            if (id != 0) return id;
        }
        return 0;
    }

    private void OnComposePublished()
    {
        Interlocked.Increment(ref publishedFrameCount);
        foreach (var entry in retired) entry.Plan.NewImagePublished();
    }

    private void TryDiscardRetired()
    {
        for (int i = retired.Count - 1; i >= 0; i--)
        {
            var entry = retired[i];
            if (!entry.Plan.CanDiscardOld(entry.Generation.Pool.ActiveReaders)) continue;
            DisposeCanvasGeneration(entry.Generation);
            entry.Plan.Discarded();
            retired.RemoveAt(i);
            settings.Trace.Add("lifecycle", "GPU", detail: "canvas.retired");
        }
    }

    private void DisposeCanvasGeneration(CanvasGeneration? generation)
    {
        if (generation == null) return;
        foreach (var surface in generation.Surfaces)
        {
            try { surface.CloseSharedHandle(); } catch (Exception e) { Fault("GPU.surfaceHandle: " + e); }
            DisposeOwned(surface, "GPU.surface");
        }
        generation.Surfaces.Clear();
    }

    private void Run()
    {
        current = new CanvasGeneration(canvas);
        using var process = Process.GetCurrentProcess();
        long cpuStartQpc = Stopwatch.GetTimestamp();
        TimeSpan cpuStart = process.TotalProcessorTime;
        // 失敗時もトレースの原点が壊れないよう、初期化前に記録する（ループ原点は初期化後に確定する）。
        settings.Trace.OriginQpc = cpuStartQpc;
        simulatedLossAtQpc = new long[settings.SimulatedDeviceLossSeconds.Count];
        for (int i = 0; i < simulatedLossAtQpc.Length; i++)
            simulatedLossAtQpc[i] = cpuStartQpc + (long)Math.Round(settings.SimulatedDeviceLossSeconds[i] * Stopwatch.Frequency);
        simulatedLossFired = new bool[simulatedLossAtQpc.Length];
        try
        {
            InitializeGpuResources(initial: true);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Loop();
                    break;
                }
                catch (GpuDeviceLostException e)
                {
                    OnDeviceLost(e);
                }
                if (recovery.Phase == GpuRecoveryPhase.Recovering)
                    AttemptRecovery();
                if (recovery.Phase == GpuRecoveryPhase.Failed && !stop.IsCancellationRequested)
                {
                    WaitForManualRetry();
                    if (recovery.Phase == GpuRecoveryPhase.Recovering)
                        AttemptRecovery();
                }
            }
        }
        catch (Exception e)
        {
            Fault(e.ToString());
        }
        finally
        {
            stop.Cancel();
            StopSpoutWorker();
            if (gpu != null) Drain(gpu, "GPU.shutdown");
            DisposeDisplay();
            DisposeOwned(gpuLoopTimer, "GPU.loopTimer"); gpuLoopTimer = null;
            string outcome = Faulted ? "faulted" : "completed";
            double cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
            long cpuEndQpc = Stopwatch.GetTimestamp();
            DrainGstDeliveryEvents(true);
            settings.Trace.Save(new OutputTraceRunSummary(outcome, displayEverAttached, spoutEverEnabled,
                canvas.Width, canvas.Height, settings.PresentMarginMs, settings.ComposeLeadMs,
                settings.SenderName, cpuSeconds, cpuStartQpc, cpuEndQpc, vblankTimerHighResolution, gpuLoopTimerHighResolution, spoutLoopTimerHighResolution),
                current.Pool, scanout, TryDiagnostics());
            gpuDone?.TrySetResult();
        }
    }

    // GPU デバイス・合成 pool・共有フェンス・プレビュー・ソースを新規作成する。復旧時は canvas を維持する。
    private void InitializeGpuResources(bool initial)
    {
        gpu = new GpuDevice(settings.AdapterLuid, Fault, fenceSync: true);
        // GStreamer shim の Adopt 用に、デバイス確定を起動側へ知らせる。
        Volatile.Write(ref devicePointer, gpu.Device.NativePointer);
        deviceReady.Set();
        shaders = new ShaderPipeline(gpu);
        if (initial)
            canvas = new CanvasSettings(settings.CanvasWidth, settings.CanvasHeight, CanvasSettings.Default.DefaultFitId);
        current = new CanvasGeneration(canvas);
        for (int i = 0; i < current.Pool.Capacity; i++)
            current.Surfaces.Add(new Surface(gpu, gpu.Texture(canvas.Width, canvas.Height, SourceSharing.FenceNt), true, SourceSharing.FenceNt));
        sharedFence = new SharedFence(gpu);
        if (initial)
        {
            originQpc = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
            settings.Trace.OriginQpc = originQpc;
            gpuLoopTimer = new VblankWaitTimer();
            gpuLoopTimerHighResolution = gpuLoopTimer.HighResolution;
        }
        CreatePreviewTargets(gpu);
        layer = new ComposeLayer(gpu, shaders, canvas);
        composeLead = new ComposeLeadController(Stopwatch.Frequency, settings.ComposeLeadMs);
        leadSuspension.Attach(composeLead);
        if (initial)
        {
            if (spoutRunning && !stop.IsCancellationRequested) StartSpoutWorker();
            Log.Information("OutputEngine: 初期化完了 canvas={W}x{H} adapterLuid={Luid}",
                canvas.Width, canvas.Height, gpu.Luid);
        }
    }

    // ── デバイス消失復旧（段階 5.2） ─────────────────────────────

    private void MaybeSimulateDeviceLoss()
    {
        if (simulatedLossAtQpc.Length == 0) return;
        long now = Stopwatch.GetTimestamp();
        for (int i = 0; i < simulatedLossAtQpc.Length; i++)
        {
            if (simulatedLossFired[i] || now < simulatedLossAtQpc[i]) continue;
            simulatedLossFired[i] = true;
            Log.Warning("OutputEngine: 試験用のデバイス消失を発生させます index={Index}", i);
            throw new GpuDeviceLostException("simulated device loss");
        }
    }

    // GpuDeviceLostException だけが Lost の入力（I9: 期限超過 fault はこの状態機械に入れない）。
    private void OnDeviceLost(GpuDeviceLostException e)
    {
        Log.Error(e, "OutputEngine: GPU デバイス消失");
        settings.Trace.Add("error", "GPU", detail: "deviceLost:" + e.Message);
        GpuRecoveryPhase phase = recovery.OnDeviceLost();
        if (phase == GpuRecoveryPhase.Lost)
        {
            if (recovery.TryAutoRecover())
                NotifyGpuStatus(GpuOutputStatus.Recovering);
            else
                NotifyGpuStatus(GpuOutputStatus.Failed);
        }
        else if (phase == GpuRecoveryPhase.Failed)
        {
            NotifyGpuStatus(GpuOutputStatus.Failed);
        }
    }

    private void AttemptRecovery()
    {
        if (RecoverCore())
        {
            recovery.OnRecovered();
            NotifyGpuStatus(GpuOutputStatus.Recovered);
        }
        else
        {
            recovery.OnRecoveryFailed();
            NotifyGpuStatus(GpuOutputStatus.Failed);
        }
    }

    private void NotifyGpuStatus(GpuOutputStatus status)
    {
        try { settings.GpuStatusChanged?.Invoke(status); }
        catch (Exception e) { Log.Warning(e, "OutputEngine: GPU 状態通知に失敗"); }
    }

    // 復旧手順の順序は GpuRecoveryPlan（管理テスト対象）を使う。
    private bool RecoverCore()
    {
        Log.Warning("OutputEngine: GPU 復旧を開始");
        settings.Trace.Add("lifecycle", "GPU", detail: "gpu.recover.start");
        foreach (GpuRecoveryStep step in GpuRecoveryPlan.Steps)
        {
            if (stop.IsCancellationRequested) return false;
            try
            {
                RunRecoveryStep(step);
            }
            catch (Exception e)
            {
                Log.Error(e, "OutputEngine: GPU 復旧の手順 {Step} に失敗", step);
                settings.Trace.Add("error", "GPU", detail: $"gpu.recover.fail:{step}");
                return false;
            }
        }
        Log.Information("OutputEngine: GPU 復旧が完了");
        settings.Trace.Add("lifecycle", "GPU", detail: "gpu.recover.done");
        return true;
    }

    private void RunRecoveryStep(GpuRecoveryStep step)
    {
        switch (step)
        {
            case GpuRecoveryStep.StopSpoutWorker:
                StopSpoutWorker();
                break;
            case GpuRecoveryStep.ReleaseLeasesAndDisposeComposeResources:
                DisposeGpuResourcesForRecovery();
                break;
            case GpuRecoveryStep.RecreateDevice:
                CreateGpuDeviceForRecovery();
                break;
            case GpuRecoveryStep.RebuildComposeResources:
                RebuildGpuResourcesForRecovery();
                break;
            case GpuRecoveryStep.RecreateFullscreenSwapchain:
                RecreateFullscreenForRecovery();
                break;
            case GpuRecoveryStep.ReinitializeSpout:
                if (spoutRunning && !stop.IsCancellationRequested) StartSpoutWorker();
                break;
            case GpuRecoveryStep.ReconnectSources:
                ReconnectSourcesForRecovery();
                break;
        }
    }

    private void CreateGpuDeviceForRecovery()
    {
        GpuDevice device;
        try
        {
            device = new GpuDevice(settings.AdapterLuid, Fault, fenceSync: true);
        }
        catch (Exception e) when (settings.AdapterLuid != null)
        {
            Log.Warning(e, "OutputEngine: 同じアダプターで再作成できないため既定アダプターを使います");
            device = new GpuDevice(null, Fault, fenceSync: true);
        }
        gpu = device;
        Volatile.Write(ref devicePointer, device.Device.NativePointer);
        deviceReady.Set();
        settings.Trace.Add("lifecycle", "GPU", detail: "gpu.device.recreated");
    }

    // 全 lease を返し、旧デバイス上の資源を破棄する（旧 gpu 自体も含む）。
    private void DisposeGpuResourcesForRecovery()
    {
        recoveryDisplayHwnd = target?.Hwnd ?? IntPtr.Zero;
        DisposeQuietly(layer); layer = null;
        DisposeCanvasGenerationForRecovery(current);
        foreach (RetiredGeneration entry in retired) DisposeCanvasGenerationForRecovery(entry.Generation);
        retired.Clear();
        // trace/shutdown が pool を参照しても壊れないよう、空の世代を保持する。
        current = new CanvasGeneration(canvas);
        DisposeQuietly(sharedFence); sharedFence = null;
        DisposeQuietly(shaders); shaders = null;
        DisposeQuietly(previewTarget); previewTarget = null;
        DisposeQuietly(previewTexture); previewTexture = null;
        DisposeQuietly(previewStaging); previewStaging = null;
        gstSource?.DropRingResourcesForRecovery();
        DisposeQuietly(vblankTimer); vblankTimer = null; vblankTimerHighResolution = null;
        DisposeQuietly(target); target = null;
        vblank = null;
        DisposeQuietly(gpu); gpu = null;
        Volatile.Write(ref devicePointer, IntPtr.Zero);
        sourceGeneration = -1;
        lastGstSequence = -1;
        lastGstFenceWaited = -1;
        lastGstGeneration = -1;
        Volatile.Write(ref firstFault, null);
        Volatile.Write(ref faulted, 0);
    }

    private void RebuildGpuResourcesForRecovery()
    {
        shaders = new ShaderPipeline(gpu!);
        current = new CanvasGeneration(canvas);
        for (int i = 0; i < current.Pool.Capacity; i++)
            current.Surfaces.Add(new Surface(gpu!, gpu!.Texture(canvas.Width, canvas.Height, SourceSharing.FenceNt), true, SourceSharing.FenceNt));
        sharedFence = new SharedFence(gpu!);
        CreatePreviewTargets(gpu!);
        layer = new ComposeLayer(gpu!, shaders, canvas);
        composeLead = new ComposeLeadController(Stopwatch.Frequency, settings.ComposeLeadMs);
        leadSuspension.Attach(composeLead);
        composeLead.SuspendLearning(Stopwatch.GetTimestamp());
        settings.Trace.Add("lifecycle", "GPU", detail: "gpu.resources.rebuilt");
    }

    private void RecreateFullscreenForRecovery()
    {
        IntPtr hwnd = recoveryDisplayHwnd;
        recoveryDisplayHwnd = IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        target = new SwapchainTarget(gpu!, hwnd);
        vblank = new VblankDisplayGate(settings.PresentMarginMs, 0, Stopwatch.Frequency);
        vblankTimer = new VblankWaitTimer();
        vblankTimerHighResolution = vblankTimer.HighResolution;
        scanout.Reset(); // 新 swapchain は PresentCount が 1 から始まる
        pendingDisplayTrace = true;
        composeLead?.SuspendLearning(Stopwatch.GetTimestamp());
        settings.Trace.Add("lifecycle", "GPU", detail: "display.recreated");
    }

    private void ReconnectSourcesForRecovery()
    {
        leadSuspension.OnSourceAttached();
        if (gstSource == null) return;
        if (gstSource.TryReopenOn(gpu!))
        {
            settings.Trace.Add("lifecycle", "GPU", detail: "gst.ring.reopened");
            return;
        }
        // 共有リングを開き直せない場合のみ、UI に player 再生成（再ロード・位置シーク・再生状態復帰）を依頼する。
        // player 破棄中の旧ハンドルへ触れないよう、依頼前に参照を落とす（再接続は UI 完了後の AttachGStreamerSource）。
        Log.Warning("OutputEngine: 共有リングを開き直せないため GStreamer player の再生成を依頼します");
        settings.Trace.Add("lifecycle", "GPU", detail: "gst.player.rebind");
        if (gstSource.TryDispose()) gstSource.Dispose();
        gstSource = null;
        gstNative = null;
        gstPlayer = IntPtr.Zero;
        try { settings.GStreamerRebindRequested?.Invoke(gpu!.Device.NativePointer); }
        catch (Exception e) { Log.Error(e, "OutputEngine: GStreamer player 再生成の依頼に失敗"); }
    }

    private void DisposeQuietly(IDisposable? resource)
    {
        try { resource?.Dispose(); }
        catch (GpuDeviceLostException) { }
        catch (Exception e) { Log.Warning(e, "OutputEngine: 復旧中の資源解放に失敗"); }
    }

    private void DisposeCanvasGenerationForRecovery(CanvasGeneration? generation)
    {
        if (generation == null) return;
        foreach (Surface surface in generation.Surfaces)
        {
            try { surface.CloseSharedHandle(); } catch (Exception e) { Log.Warning(e, "OutputEngine: 復旧時の共有ハンドル解放に失敗"); }
            DisposeQuietly(surface);
        }
        generation.Surfaces.Clear();
    }

    private void WaitForManualRetry()
    {
        Log.Warning("OutputEngine: GPU 出力停止。再試行を待ちます");
        while (!stop.IsCancellationRequested && recovery.Phase == GpuRecoveryPhase.Failed)
        {
            bool requested;
            lock (recoveryGate)
            {
                requested = manualRetryRequested;
                manualRetryRequested = false;
            }
            if (requested && recovery.TryManualRetry())
            {
                NotifyGpuStatus(GpuOutputStatus.Recovering);
                return;
            }
            recoveryRetry.Wait(200);
            recoveryRetry.Reset();
        }
    }

    private void CreatePreviewTargets(GpuDevice device)
    {
        const int width = 960, height = 540;
        previewTexture = device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            MiscFlags = ResourceOptionFlags.None
        });
        previewTarget = device.Device.CreateRenderTargetView(previewTexture);
        previewStaging = device.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        });
    }

    // ── GPU ループ ────────────────────────────────────────────────

    private void Loop()
    {
        long frequency = Stopwatch.Frequency;
        var schedule = new TickSchedule(originQpc, settings.Fps, frequency, scheduleOffset);
        composeSchedule = schedule;
        long lastScheduled = long.MinValue;
        while (!stop.IsCancellationRequested)
        {
            ProcessCommands();
            MaybeSimulateDeviceLoss();
            TryDiscardRetired();
            // 長時間 run でも gst.delivery を欠落させないよう、250ms 間隔で drain する。
            DrainGstDeliveryEvents(false);
            long now = Stopwatch.GetTimestamp();
            long due = schedule.DueQpc;
            if (vblank != null && VblankIdle(lastScheduled, now, due)) continue;
            if (now < due)
            {
                if (LoopIdleWait.UseTimer(now, due, frequency))
                    gpuLoopTimer!.WaitUntilOrStop(stop.Token.WaitHandle, now, due, frequency);
                else Thread.Yield();
                continue;
            }
            var tick = schedule.Take(now);
            lastScheduled = tick.Scheduled;
            if (tick.Skipped > 0) settings.Trace.Add("skip", "GPU", tick.Scheduled, detail: "schedule.late", value: tick.Skipped);
            ComposeTick(tick.Scheduled, schedule.DueQpc);
        }
    }

    // vblank の待ちと Present 判断（試作 ProbeEngine の idle と同じ順序）。
    private bool VblankIdle(long slot, long now, long due)
    {
        if (vblank == null || target == null) return false;
        long latestId = LatestVisibleId();
        var (step, deadline, kind) = vblank.Decide(slot, now, due, latestId, stop.IsCancellationRequested);
        if (step == VblankStep.Idle && vblank.Pending is { } pending && now < pending.TargetQpc && pending.TargetQpc < due)
            (step, deadline, kind) = (VblankStep.WaitTarget, pending.TargetQpc, "target");
        if (step == VblankStep.WaitTarget)
        {
            vblank.Wait(deadline, kind, Stopwatch.GetTimestamp,
                (startQpc, dueQpc) => vblankTimer!.WaitUntilOrStop(stop.Token.WaitHandle, startQpc, dueQpc, Stopwatch.Frequency),
                attempt =>
                {
                    settings.Trace.Record(new("display.vblank.wait.start", "GPU", attempt.StartQpc, slot, Detail: attempt.Kind, Value: attempt.RequestedMicroseconds, DeadlineQpc: attempt.DeadlineQpc));
                    settings.Trace.Record(new("display.vblank.wait.end", "GPU", attempt.EndQpc, slot, Detail: attempt.Outcome, Value: attempt.LatenessMicroseconds, DeadlineQpc: attempt.DeadlineQpc));
                });
            return true;
        }
        if (step == VblankStep.Present)
        {
            var prediction = vblank.AttemptPrediction!.Value;
            settings.Trace.Record(new("display.vblank.predict", "GPU", Stopwatch.GetTimestamp(), slot, latestId,
                Detail: (prediction.PeriodTicks * 1_000_000 / Stopwatch.Frequency).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Value: prediction.PredictedRefresh, DeadlineQpc: prediction.VblankQpc));
            if (HoldVblankReadiness(target, slot, prediction.VblankQpc - vblank.LeadTicks))
            {
                Present(slot, due);
            }
            ObserveScanout();
            return true;
        }
        return false; // Stop / Compose / Idle / Bootstrap。Bootstrap は合成 tick から出す。
    }

    private void ComposeTick(long scheduled, long nextScheduled)
    {
        int slot = current.Pool.TryBeginWrite();
        if (slot < 0) { settings.Trace.Add("skip", "GPU", scheduled, detail: "compose.noFreeSlot", value: 1); return; }
        var surface = current.Surfaces[slot];

        // タイムライン状態を取り込む（最新1件、UI は待たない）。
        // スナップショットのアップロードは Loop の空き時間で完了済みで、ここでは取得だけを行う。
        var state = timelineInput.Take();
        if (state != null) lastTimelineState = state;
        var effective = state ?? lastTimelineState;
        int generation = effective?.Generation ?? sourceGeneration;
        if (generation >= 0) EnsureSourceGeneration(generation);
        double position = effective?.PositionSeconds ?? 0;
        LayerImage? acquired = null;
        ISourceImageLease? lease = null;
        SourceStatus status = SourceStatus.NotReady;

        if (gstSource != null)
        {
            // GStreamer: shim のリーステクスチャを SRV で直接描画する（CPU/GPU コピーを挟まない）。
            long acquireStartedQpc = Stopwatch.GetTimestamp();
            SyncGStreamerGeneration();
            var gst = AcquireGStreamer(position);
            long acquireEndedQpc = Stopwatch.GetTimestamp();
            status = gst.Status;
            lease = gst.Lease;
            acquired = gst.Image;
            if (settings.Trace.IsEnabled)
                settings.Trace.Record(new("compose.acquire", "GPU", acquireEndedQpc, scheduled,
                    gst.Stamp.Sequence, gst.Stamp.DecodedQpc, status.ToString(),
                    (acquireEndedQpc - acquireStartedQpc) * 1_000_000 / Stopwatch.Frequency,
                    PtsNs: SourceStampPtsNs(gst.Stamp.PositionSeconds)));
            settings.Trace.Add("source.acquire", "GPU", scheduled,
                new ImageStamp(gst.Stamp.Sequence, gst.Stamp.DecodedQpc), status.ToString(),
                (long)Math.Round(position * 1_000_000));
            if (status == SourceStatus.Ready)
                settings.SourceFrameReady?.Invoke(acquireEndedQpc, (int)gst.Stamp.Generation, gst.Stamp.Sequence,
                    gst.Stamp.PositionSeconds);
            if (status == SourceStatus.Ended)
                settings.Trace.Add("skip", "GPU", scheduled, detail: "compose.sourceEnded", value: 1);
            else if (status != SourceStatus.Ready && (effective?.Gap ?? OutputGapMode.None) == OutputGapMode.None)
                settings.Trace.Add("skip", "GPU", scheduled, detail: "compose.sourceNotReady", value: 1);
        }
        else
        {
            // 段 2: mpv 経路は削除。GStreamer ソース未接続（起動〜接続、player 再生成待ち）は
            // 明示的な NotReady とし、ComposeLayerPolicy が Held（無ければギャップ規則）を描く。
            long acquireEndedQpc = Stopwatch.GetTimestamp();
            status = SourceStatus.NotReady;
            if (settings.Trace.IsEnabled)
                settings.Trace.Record(new("compose.acquire", "GPU", acquireEndedQpc, scheduled,
                    0, 0, status.ToString(), 0, PtsNs: 0));
            settings.Trace.Add("source.acquire", "GPU", scheduled, default, status.ToString(),
                (long)Math.Round(position * 1_000_000));
            if ((effective?.Gap ?? OutputGapMode.None) == OutputGapMode.None)
                settings.Trace.Add("skip", "GPU", scheduled, detail: "compose.sourceNotConnected", value: 1);
        }

        bool writing = true, inFlight = false, retained = false;
        try
        {
            var stamp = new ImageStamp(++nextImageId, Stopwatch.GetTimestamp());
            long composeStartedQpc = Stopwatch.GetTimestamp();
            settings.Trace.Add("compose.start", "GPU", scheduled, stamp);
            if (lease != null) { lease.BeginGpuUse(); inFlight = true; }
            // D5 決定再現: フック有効時は切替後の最初のフレームまで Black を強制する（既定は上書きなし）。
            OutputGapMode gapMode = effective?.Gap ?? OutputGapMode.None;
            if (armedForceGapBlack) gapMode = OutputGapMode.Black;
            retained = layer!.Compose(surface,
                gapMode,
                effective?.Clip ?? new ClipPlacement(null),
                effective?.TestCardEnabled ?? testCard,
                stamp, originQpc, acquired);
            if (forceGapBlackOnSwitch && acquired != null)
            {
                anyFrameAcquired = true;
                armedForceGapBlack = false;
            }
            long composeDrawQpc = Stopwatch.GetTimestamp();
            sharedFence!.Signal(gpu!, stamp.Id); // フェンス値＝画像 ID。Spout 側は GPU キューで待つ。
            gpu!.Fence.Wait("compose.source");
            long composeCompletedQpc = Stopwatch.GetTimestamp();
            if (settings.Trace.IsEnabled)
            {
                settings.Trace.Record(new("compose.draw", "GPU", composeDrawQpc, scheduled, stamp.Id, stamp.GeneratedQpc,
                    Value: (composeDrawQpc - composeStartedQpc) * 1_000_000 / Stopwatch.Frequency));
                settings.Trace.Record(new("compose.fence", "GPU", composeCompletedQpc, scheduled, stamp.Id, stamp.GeneratedQpc,
                    Value: (composeCompletedQpc - composeDrawQpc) * 1_000_000 / Stopwatch.Frequency));
            }
            if (inFlight) { lease!.CompleteGpuUse(); inFlight = false; }
            UpdateComposeLead(composeCompletedQpc - composeStartedQpc, composeCompletedQpc);
            settings.Trace.Add("compose.complete", "GPU", scheduled, stamp);
            long publishedTicks = Stopwatch.GetTimestamp();
            // B4 の厳密結合: accuracy の frame イベント（RecordGpuAccuracyFrame）へ渡す
            // publishedTicks と同一の QPC を compose.publish に使う。frameTicks ==
            // compose.publish.qpc となり、全標本を完全一致で結べる（従来は別々の
            // GetTimestamp で ±2ms の近似結合だった）。
            settings.Trace.Record(new("compose.publish", "GPU", publishedTicks, scheduled, stamp.Id, stamp.GeneratedQpc));
            current.Pool.Publish(slot, stamp, true);
            OnComposePublished();
            writing = false;
            settings.Trace.Record(new("compose.visible", "GPU", Stopwatch.GetTimestamp(), scheduled, stamp.Id, stamp.GeneratedQpc));
            // A1: 計測有効時のみ、公開したフレームの画素マーカーを読み戻して記録する（既定経路は IsEnabled 読みだけ）。
            // 新規ソースがあればソースを、Held/黒/カードの tick は合成キャンバス（レンダラが公開した画素）を読む。
            if (acquired != null)
                RecordGpuAccuracyFrame(acquired.Value.RawTexture, acquired.Value.Width, acquired.Value.Height,
                    publishedTicks, "gpu");
            else if (SyncAccuracyTrace.Current.IsEnabled)
                RecordGpuAccuracyFrame(surface.Texture.NativePointer, canvas.Width, canvas.Height,
                    publishedTicks, "gpu-canvas");
            // D5: GStreamer のリースは LayerImage が持たない（共有リング画像は Lease=null）ため、
            // ここで lease を null にして捨てると返却漏れになる。finally の lease?.Dispose() に返させる
            // （mpv は LayerImage.Release() が返却済みで、その Dispose は冪等）。
            if (!retained && acquired != null) { acquired.Value.Release(); acquired = null; }
        }
        finally
        {
            if (inFlight) lease!.CompleteGpuUse();
            if (!retained && acquired != null) acquired.Value.Release();
            // GStreamer のリースは shim が最新へ進めるよう毎 tick 返す（描画テクスチャは AddRef 済み）。
            if (gstSource != null) lease?.Dispose();
            if (writing) { try { gpu!.Fence.Wait("compose.drain"); } finally { current.Pool.AbortWrite(slot, true); } }
        }

        if (stop.IsCancellationRequested) return;
        if (target != null && vblank != null && !vblank.HasScanout)
        {
            long now = Stopwatch.GetTimestamp();
            if (vblank.Decide(scheduled, now, nextScheduled, LatestVisibleId(), stop.IsCancellationRequested).Step == VblankStep.Bootstrap)
            {
                settings.Trace.Add("display.vblank.bootstrap", "GPU", scheduled, value: 1);
                if (HoldVblankReadiness(target, scheduled)) Present(scheduled, nextScheduled);
            }
        }
        if (target != null) ObserveScanout();
        UpdatePreview(Stopwatch.GetTimestamp());
    }

    // 取得画像のスタンプ PTS（秒）を ns へ。負値・非有限は 0（未取得）にする。
    private static long SourceStampPtsNs(double positionSeconds) =>
        double.IsFinite(positionSeconds) && positionSeconds >= 0
            ? (long)Math.Round(positionSeconds * 1_000_000_000.0)
            : 0;

    // A1: 計測有効時のみ動く GPU 経路の精度プローブ。公開したソーステクスチャの画素を読み戻し、
    // CPU 経路と同一の AccuracyFrameMarker でフレームを同定して精度トレースへ記録する。
    // 既定経路（IsEnabled=false）ではここへ入らず、ステージングも読み戻しも発生しない。
    private void RecordGpuAccuracyFrame(IntPtr rawTexture, int width, int height, long publishedTicks, string kind)
    {
        var trace = SyncAccuracyTrace.Current;
        if (!trace.IsEnabled || rawTexture == IntPtr.Zero || width <= 0 || height <= 0) return;
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (accuracyStaging == null || accuracyStagingWidth != width || accuracyStagingHeight != height)
            {
                accuracyStaging?.Dispose();
                var description = new Texture2DDescription
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new(1, 0),
                    Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None,
                    CPUAccessFlags = CpuAccessFlags.Read
                };
                accuracyStaging = gpu!.Device.CreateTexture2D(description);
                accuracyStagingWidth = width;
                accuracyStagingHeight = height;
            }
            using var source = NativeTextureOps.OpenOwned(rawTexture, pointer => new ID3D11Texture2D(pointer));
            gpu!.Context.CopyResource(accuracyStaging, source);
            var mapped = gpu.Context.Map(accuracyStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            AccuracyFrameProbe probe;
            try { probe = AccuracyFrameMarker.Probe(mapped.DataPointer, width, height, (int)mapped.RowPitch); }
            finally { gpu.Context.Unmap(accuracyStaging, 0); }
            trace.RecordGpuFrame(kind, width, height, probe, publishedTicks,
                Stopwatch.GetTimestamp() - started);
        }
        catch (Exception e)
        {
            Log.Warning(e, "OutputEngine: 精度プローブの読み戻しに失敗");
        }
    }

    private void UpdateComposeLead(long durationTicks, long nowQpc)
    {
        if (composeLead == null || align == null) return;
        if (!composeLead.Add(durationTicks, nowQpc, out double newLeadMs)) return;
        align.SetLeadMilliseconds(newLeadMs);
        settings.Trace.Add("lifecycle", "GPU",
            detail: "composeLeadMs:" + newLeadMs.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
        Log.Information("OutputEngine: compose lead を {Lead:F2}ms へ更新", newLeadMs);
    }

    private SourceDiagnostics? TryDiagnostics()
    {
        try
        {
            // GStreamer 接続時は shim 側のデコーダ・世代排除・ready 数をトレースへ出す。
            if (gstSource != null) return gstSource.Diagnostics;
            return null;
        }
        catch (Exception) { return null; }
    }

    private void EnsureSourceGeneration(int generation)
    {
        if (generation < 0 || generation == sourceGeneration) return;
        sourceGeneration = generation;
        // GStreamer の世代は shim 側の値を観測して対応付ける（SyncGStreamerGeneration）。
        layer!.ClearFreeze();
        // L-3: 世代変更（load/seek 等）の直後は位相が乱れるため lead 学習を 1 秒除外する。
        leadSuspension.OnSourceGenerationChanged();
    }

    private readonly record struct GstFrameAcquire(SourceStatus Status, ISourceImageLease? Lease, LayerImage? Image, SourceImageStamp Stamp);

    // GStreamer の世代は shim 側の値（load/seek で進む）を観測して対応付ける。
    // D26: Held は合成側が所有する直前キャンバスの複製で、世代のリング面を参照しない。
    // 世代が変わっても破棄せず、新しい世代の最初のフレームまで直前の絵を出す（黒を挟まない）。
    private void SyncGStreamerGeneration()
    {
        if (gstSource == null) return;
        int shimGeneration = gstSource.Generation;
        if (shimGeneration == lastGstGeneration) return;
        lastGstGeneration = shimGeneration;
        lastGstSequence = -1;
        // D5 決定再現: 再生開始後に世代が変わったら、その世代の最初のフレームまで Black を強制する。
        if (forceGapBlackOnSwitch && anyFrameAcquired)
            armedForceGapBlack = true;
        // L-3: gst.generation（load/seek）の直後は位相が乱れるため lead 学習を 1 秒除外する。
        leadSuspension.OnSourceGenerationChanged();
        settings.Trace.Add("lifecycle", "GPU", detail: $"gst.generation:{shimGeneration}");
    }

    // shim の配信イベント（on_new_sample 到着 QPC、callback 所要、置換など）を events.jsonl へ写す。
    // qpc は shim 側の QueryPerformanceCounter で、events.jsonl と同じ時計。
    private void DrainGstDeliveryEvents(bool force)
    {
        if (gstNative == null || gstPlayer == IntPtr.Zero) return;
        long now = Stopwatch.GetTimestamp();
        if (!force && now - gstDeliveryDrainQpc < Stopwatch.Frequency / 4) return;
        gstDeliveryDrainQpc = now;
        try
        {
            GstNative.TcsDeliveryEvent[] buffer = gstDeliveryBuffer ??= new GstNative.TcsDeliveryEvent[256];
            while (true)
            {
                if (gstNative.DrainDeliveryEvents(gstPlayer, buffer, (uint)buffer.Length, out uint count) != 0 || count == 0) return;
                for (int i = 0; i < count; i++)
                {
                    // flags bit 3 は GetTimePos 同時点の position スナップショット（gst.position）。
                    settings.Trace.Record(GstDeliveryTraceMapper.Map(buffer[i]));
                }
                if (count < buffer.Length) return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OutputEngine: 配信トレースの取得に失敗");
        }
    }

    // GStreamer shim の最新リースを取得し、直接描画できる形で返す。
    // shim は「リース保持中は同じ画像を返す」ため、リースは compose 後に毎回返す（呼び出し側が Dispose）。
    // ステージ 6b: slot>=0 のリースは共有リング Surface を使い、描画前に共有フェンスを GPU キューで待つ
    // （CPU は待たない）。D8: リング外のリースは GStreamerSource が拒否するためここには来ない。
    private GstFrameAcquire AcquireGStreamer(double position)
    {
        int generation = gstSource!.Generation;
        var status = gstSource.TryAcquire(generation, position, out var lease);
        if (status != SourceStatus.Ready || lease == null) return new(status, null, null, default);
        var stamp = lease.Stamp;
        if (stamp.Sequence == lastGstSequence) return new(status, lease, null, stamp);
        var gstLease = (GStreamerSource.Lease)lease;
        if (gstLease.Slot >= 0 && layer!.HasHeld && !gstSource.IsRingFenceComplete(stamp.Sequence))
        {
            // I1/I5: 共有リングのコピー完了（IDR デコード等で数 ms 遅れる）を合成 tick の GPU
            // フェンス待ちに含めない。この tick は直前の Held を描き、完了後に最新フレームを使う。
            return new(status, lease, null, stamp);
        }
        lastGstSequence = stamp.Sequence;
        long srvStartedQpc = Stopwatch.GetTimestamp();
        ID3D11Texture2D? ringTexture = null;
        ID3D11ShaderResourceView? ringView = null;
        bool useRing = gstLease.Slot >= 0
            && gstSource.TryGetRingSurface(gstLease.Slot, out ringTexture, out ringView);
        long srvEndedQpc = Stopwatch.GetTimestamp();
        if (settings.Trace.IsEnabled)
            settings.Trace.Record(new("compose.srv", "GPU", srvEndedQpc,
                Detail: gstLease.Slot.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Value: (srvEndedQpc - srvStartedQpc) * 1_000_000 / Stopwatch.Frequency));
        if (useRing)
        {
            if (GstRingPolicy.ShouldWaitFence(gstLease.Slot, stamp.Sequence, lastGstFenceWaited))
            {
                long waitStartedQpc = Stopwatch.GetTimestamp();
                gstSource.WaitRingFence((ulong)stamp.Sequence);
                long waitEndedQpc = Stopwatch.GetTimestamp();
                if (settings.Trace.IsEnabled)
                    settings.Trace.Record(new("compose.ringWait", "GPU", waitEndedQpc,
                        Detail: stamp.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Value: (waitEndedQpc - waitStartedQpc) * 1_000_000 / Stopwatch.Frequency));
                lastGstFenceWaited = stamp.Sequence;
            }
            // リング Surface は GStreamerSource が所有する（LayerImage は借用して渡すだけ）。
            var ringImage = new LayerImage(ringView!, ringTexture!.NativePointer, lease.Width, lease.Height, null, null);
            return new(status, lease, ringImage, stamp);
        }
        // D8: リング外のリースを GPU 合成で描かない（GStreamerSource が Reject する）。
        // ここに来るのはリング未接続・範囲外などで、画像無し（Held）として返す。
        return new(status, lease, null, stamp);
    }

    /// <summary>キャンバス 1 世代分の合成 pool と共有サーフェス。</summary>
    private sealed class CanvasGeneration(CanvasSettings canvas)
    {
        public CanvasSettings Canvas { get; } = canvas;
        public LatestPool Pool { get; } = new(3);
        public List<Surface> Surfaces { get; } = new();
    }

    /// <summary>旧世代と、その破棄可否を判断する plan。</summary>
    private sealed class RetiredGeneration(CanvasGeneration generation)
    {
        public CanvasGeneration Generation { get; } = generation;
        public CanvasSwapPlan Plan { get; } = new();
    }

    private void UpdatePreview(long now)
    {
        long period = Stopwatch.Frequency / (target != null ? 10 : 30);
        if (now - lastPreviewQpc < period) return;
        if (!previewHandoff.TryAcquire(out byte[]? buffer)) return;
        byte[] pixels = buffer!;
        bool handedOff = false;
        try
        {
            var (lease, generation) = AcquireLatestImage();
            if (lease == null || generation == null) return;
            try
            {
                lease.BeginGpuUse();
                bool inFlight = true;
                try
                {
                    shaders!.Display(generation.Surfaces[lease.Slot], previewTarget!, 960, 540, generation.Canvas.Width, generation.Canvas.Height);
                    gpu!.Fence.Wait("preview.draw");
                    lease.CompleteGpuUse(); inFlight = false;
                }
                finally
                {
                    if (inFlight) { gpu!.Fence.Wait("preview.drain"); lease.CompleteGpuUse(); }
                }
                gpu!.Context.CopyResource(previewStaging!, previewTexture!);
                var mapped = gpu.Context.Map(previewStaging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    unsafe
                    {
                        byte* source = (byte*)mapped.DataPointer;
                        int rowBytes = 960 * 4;
                        for (int y = 0; y < 540; y++)
                        {
                            Marshal.Copy((IntPtr)(source + y * mapped.RowPitch), pixels, y * rowBytes, rowBytes);
                        }
                    }
                }
                finally { gpu.Context.Unmap(previewStaging!, 0); }
                lastPreviewQpc = now;
                var callback = settings.PreviewFrameReady;
                if (callback != null)
                {
                    var frame = new PreviewFrame(pixels, 960, 540, () => previewHandoff.Release(pixels));
                    handedOff = true;
                    callback(frame);
                }
            }
            finally { lease.Dispose(); }
        }
        finally { if (!handedOff) previewHandoff.Release(pixels); }
    }

    private const int MaxVblankReadyWaitMs = 4;

    // D-2: 即時 Present では latency waitable を vblank−lead までタイムアウト付きで待つ。
    // それでも未シグナルなら予測を捨て、notReady の記録は 1 tick 最大 1 回にする。
    private bool HoldVblankReadiness(SwapchainTarget swapchain, long slot, long waitUntilQpc = 0)
    {
        if (swapchain.Readiness.PermissionHeld) return true;
        int timeoutMs = waitUntilQpc > 0
            ? VblankDisplayGate.TimeoutUntilMs(Stopwatch.GetTimestamp(), waitUntilQpc, Stopwatch.Frequency, MaxVblankReadyWaitMs)
            : 0;
        if (swapchain.WaitReady(timeoutMs)) { swapchain.Readiness.GrantFromNotification(); return true; }
        if (notReadyGate.ShouldRecord(slot))
            settings.Trace.Add("skip", "GPU", slot, detail: "display.vblank.notReady", value: 1);
        vblank!.Defer();
        return false;
    }

    private void Present(long scheduled, long nextScheduled)
    {
        long deadline = vblank?.PresentDeadline(nextScheduled, long.MaxValue) ?? nextScheduled;
        vblank?.BeginAttempt(scheduled);
        long selectStarted = Stopwatch.GetTimestamp();
        var acquired = AcquireLatestImage();
        using var lease = acquired.Lease;
        CanvasGeneration? generation = acquired.Generation;
        long selectEnded = Stopwatch.GetTimestamp();
        settings.Trace.Record(new("display.select.start", "GPU", selectStarted, scheduled, lease?.Stamp.Id ?? 0, lease?.Stamp.GeneratedQpc ?? 0, DeadlineQpc: deadline));
        ImageStamp selected = lease?.Stamp ?? default;
        settings.Trace.Record(new("display.select.end", "GPU", selectEnded, scheduled, selected.Id, selected.GeneratedQpc,
            lease == null ? "none" : "latest", DeadlineQpc: deadline));
        if (lease == null || generation == null) return;
        string? stale = vblank?.SkipReason(lease.Stamp.Id);
        if (stale != null) { settings.Trace.Add("skip", "GPU", scheduled, lease.Stamp, stale, 1); return; }
        settings.Trace.Record(new("display.fence.wait", "GPU", Stopwatch.GetTimestamp(), scheduled, lease.Stamp.Id, lease.Stamp.GeneratedQpc,
            Detail: lease.Slot.ToString(), Value: lease.Stamp.Id));
        Surface source = generation.Surfaces[lease.Slot];
        bool inFlight = false;
        try
        {
            long drawStarted = Stopwatch.GetTimestamp();
            lease.BeginGpuUse(); inFlight = true;
            try { shaders!.Display(source, target!.Target, target.Width, target.Height, generation!.Canvas.Width, generation.Canvas.Height); }
            finally { settings.Trace.Record(new("display.draw.start", "GPU", drawStarted, scheduled, lease.Stamp.Id, lease.Stamp.GeneratedQpc)); }
            gpu!.Fence.Wait("display.draw");
            lease.CompleteGpuUse(); inFlight = false;
            settings.Trace.Add("display.draw.complete", "GPU", scheduled, lease.Stamp);
        }
        finally
        {
            if (inFlight) { try { gpu!.Fence.Wait("display.draw.drain"); } finally { lease.CompleteGpuUse(); } }
        }
        var displayed = lease.Stamp;
        lease.Dispose();
        long presentStarted = Stopwatch.GetTimestamp();
        int result;
        long presentReturned;
        try { result = target!.Present(); presentReturned = Stopwatch.GetTimestamp(); }
        finally { settings.Trace.Record(new("present.start", "GPU", presentStarted, scheduled, displayed.Id, displayed.GeneratedQpc)); }
        vblank?.Presented(displayed.Id);
        if (result == 0)
        {
            if (pendingDisplayTrace)
            {
                pendingDisplayTrace = false;
                settings.Trace.Add("lifecycle", "GPU", detail: $"display.attach:{target!.Width}x{target.Height}");
            }
            uint presentCount = target!.GetLastPresentCount();
            settings.Trace.Record(new("present.return", "GPU", presentReturned, scheduled, displayed.Id, displayed.GeneratedQpc, Value: presentCount));
            scanout.Record(presentCount, displayed.Id, displayed.GeneratedQpc, presentStarted);
        }
        else settings.Trace.Add("skip", "GPU", scheduled, displayed, $"present.status:0x{result:X8}", 1);
    }

    private void ObserveScanout()
    {
        if (target == null) return;
        if (target.TryGetFrameStatistics(out var stats))
        {
            long observedQpc = Stopwatch.GetTimestamp();
            var seen = scanout.Observe(stats.PresentCount, stats.PresentRefreshCount, stats.SyncRefreshCount, stats.SyncQPCTime);
            if (seen is { } s)
            {
                settings.Trace.Record(new("present.scanout", "GPU", observedQpc, 0, s.ImageId, s.GeneratedQpc, s.Detail, s.SyncRefreshCount, s.SyncQpcTime));
                vblank?.ObserveScanout(s.SyncQpcTime, s.SyncRefreshCount);
                AlignCompose();
            }
        }
        else if (scanout.NoteDisjoint()) settings.Trace.Add("present.stats.disjoint", "GPU", detail: "disjoint");
    }

    private void AlignCompose()
    {
        if (vblank == null || composeSchedule == null) return;
        long now = Stopwatch.GetTimestamp();
        if (align.Decide(vblank, now, composeSchedule.DueQpc) is not { } d) return;
        scheduleOffset.Add(d.CorrectionTicks);
        settings.Trace.Record(new("compose.align", "GPU", now, Detail: d.CorrectionMicroseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Value: d.ErrorMicroseconds, DeadlineQpc: d.WantedQpc));
    }

    private void DisposeDisplay()
    {
        DisposeOwned(vblankTimer, "GPU.vblankTimer"); vblankTimer = null; vblankTimerHighResolution = null;
        DisposeOwned(target, "GPU.display"); target = null;
        vblank = null;
    }

    private void Drain(GpuDevice device, string stage)
    {
        try { device.Fence.Wait(stage); }
        catch (GpuDeviceLostException e) { Fault(e.Message); }
    }

    private void DisposeOwned(IDisposable? resource, string stage)
    {
        try { resource?.Dispose(); }
        catch (Exception e) { Fault(stage + ": " + e); }
    }

    // ── Spout worker ──────────────────────────────────────────────

    private void StartSpoutWorker()
    {
        spoutStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var ready = new ManualResetEventSlim(false);
        spoutReady = ready;
        var token = spoutStop.Token;
        spoutThread = new Thread(() => SpoutRun(token, ready)) { Name = "OutputEngine.Spout", IsBackground = true };
        spoutThread.SetApartmentState(ApartmentState.MTA);
        spoutCpuStartTicks = Stopwatch.GetTimestamp();
        spoutThread.Start();
        // 送信側デバイス・共有資源の準備を待つ（GPU worker 上）。失敗時も finally が ready を立てる。
        if (!ready.Wait(TimeSpan.FromSeconds(10)))
            Fault("Spout worker: 初期化待ちがタイムアウトしました");
    }

    private void StopSpoutWorker()
    {
        var thread = spoutThread;
        if (thread == null) return;
        spoutStop!.Cancel();
        thread.Join();
        spoutThread = null;
        spoutStop.Dispose(); spoutStop = null; spoutReady = null;
    }

    private void SpoutRun(CancellationToken token, ManualResetEventSlim ready)
    {
        GpuDevice? sendGpu = null;
        SpoutSender? sender = null;
        SharedFenceReader? fenceReader = null;
        VblankWaitTimer? loopTimer = null;
        var opened = new List<Surface>();
        try
        {
            // キャンバス変更時は worker ごと作り直すため、開始時点の世代を捕捉する。
            CanvasGeneration generation = current;
            sendGpu = new GpuDevice(gpu!.Luid, Fault, fenceSync: true);
            foreach (var surface in generation.Surfaces)
                opened.Add(new Surface(sendGpu, sendGpu.Device1.OpenSharedResource1<ID3D11Texture2D>(surface.Handle), false, SourceSharing.None));
            fenceReader = new SharedFenceReader(sendGpu, sharedFence!.Open(sendGpu));
            sender = new SpoutSender(sendGpu, settings.Trace, "Spout", settings.SenderName,
                generation.Canvas.Width, generation.Canvas.Height, MutexWaitPolicy.MaxWaitMs, fenceReader);
            loopTimer = new VblankWaitTimer();
            spoutLoopTimerHighResolution = loopTimer.HighResolution;
            ready.Set();
            if (token.IsCancellationRequested) return;
            Surface[] reads = opened.ToArray();
            long origin = originQpc + (long)Math.Round(settings.SendPhaseMs * Stopwatch.Frequency / 1000);
            var schedule = new TickSchedule(origin, settings.Fps, Stopwatch.Frequency, scheduleOffset);
            while (!token.IsCancellationRequested)
            {
                long now = Stopwatch.GetTimestamp();
                long due = schedule.DueQpc;
                if (now < due)
                {
                    if (LoopIdleWait.UseTimer(now, due, Stopwatch.Frequency)) loopTimer.WaitUntilOrStop(token.WaitHandle, now, due, Stopwatch.Frequency);
                    else Thread.Yield();
                    continue;
                }
                var tick = schedule.Take(now);
                if (tick.Skipped > 0) settings.Trace.Add("skip", "Spout", tick.Scheduled, detail: "schedule.late", value: tick.Skipped);
                sender.Update(reads, generation.Pool, tick.Scheduled, token);
                sender.Send(tick.Scheduled, schedule.DueQpc, token);
            }
        }
        catch (Exception e)
        {
            Fault("Spout worker: " + e);
        }
        finally
        {
            ready.Set();
            if (sendGpu != null) Drain(sendGpu, "Spout.shutdown");
            DisposeOwned(sender, "Spout.sender");
            DisposeOwned(loopTimer, "Spout.loopTimer");
            foreach (var surface in opened) DisposeOwned(surface, "Spout.shared");
            DisposeOwned(fenceReader, "Spout.fence");
            DisposeOwned(sendGpu, "Spout.device");
            settings.Trace.Add("lifecycle", "Spout", detail: "worker.finished");
        }
    }

    /// <summary>
    /// 合成停止→Spout worker join→表示停止→GPU 完了待ち→ソース lease 全返却。呼び出し元をブロックする。
    /// GStreamer の shim player destroy より先に全 lease を返す順序をここで保証する。
    /// </summary>
    public void Stop()
    {
        if (!started) return;
        stop.Cancel();
        gpuDone?.Task.Wait();
        StopSpoutWorker();
        ReleaseSourceLeases();
    }

    /// <summary>GPU ドレイン後にソース lease を返す（ComposeLayer の held と GStreamer の pending を解放）。</summary>
    private void ReleaseSourceLeases()
    {
        DisposeOwned(layer, "GPU.composeLayer"); layer = null;
        if (gstSource != null && !gstSource.TryDispose())
            Log.Warning("OutputEngine: GStreamerSource の lease が停止時に残っています");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        DisposeOwned(layer, "GPU.composeLayer"); layer = null;
        DisposeOwned(gstSource, "GPU.gstreamerSource"); gstSource = null;
        Volatile.Write(ref devicePointer, IntPtr.Zero);
        DisposeCanvasGeneration(current);
        foreach (var entry in retired) DisposeCanvasGeneration(entry.Generation);
        retired.Clear();
        DisposeOwned(sharedFence, "GPU.fence"); sharedFence = null;
        DisposeOwned(shaders, "GPU.shaders"); shaders = null;
        DisposeOwned(previewTarget, "GPU.previewTarget"); previewTarget = null;
        DisposeOwned(previewTexture, "GPU.previewTexture"); previewTexture = null;
        DisposeOwned(previewStaging, "GPU.previewStaging"); previewStaging = null;
        DisposeOwned(accuracyStaging, "GPU.accuracyStaging"); accuracyStaging = null;
        DisposeOwned(gpu, "GPU.device"); gpu = null;
        recoveryRetry.Dispose();
        stop.Dispose();
    }

    private sealed class PreviewHandoff(int count)
    {
        private readonly byte[]?[] buffers = new byte[count][];
        private readonly int[] busy = new int[count];

        public bool TryAcquire(out byte[]? buffer)
        {
            for (int i = 0; i < buffers.Length; i++)
            {
                if (Interlocked.CompareExchange(ref busy[i], 1, 0) != 0) continue;
                buffers[i] ??= new byte[960 * 540 * 4];
                buffer = buffers[i];
                return true;
            }
            buffer = null;
            return false;
        }

        public void Release(byte[]? buffer)
        {
            if (buffer == null) return;
            for (int i = 0; i < buffers.Length; i++)
            {
                if (!ReferenceEquals(buffers[i], buffer)) continue;
                Volatile.Write(ref busy[i], 0);
                return;
            }
        }
    }
}

/// <summary>表示デバイス名からアダプター LUID を引く（見つからなければ null＝既定アダプター）。</summary>
internal static class OutputDisplays
{
    public static long? FindAdapterLuid(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return null;
        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success; ai++)
            {
                using (adapter)
                {
                    for (uint oi = 0; adapter.EnumOutputs(oi, out var output).Success; oi++)
                    {
                        using (output)
                        {
                            if (string.Equals(output.Description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                                return adapter.Description1.Luid;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "アダプター LUID の解決に失敗: {Display}", deviceName);
        }
        return null;
    }
}
