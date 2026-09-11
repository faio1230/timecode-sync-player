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
    public int CanvasWidth { get; init; } = 1920;
    public int CanvasHeight { get; init; } = 1080;
    public double Fps { get; init; } = 60;
    public double PresentMarginMs { get; init; } = 3;
    public double ComposeLeadMs { get; init; } = 3;
    public double SendPhaseMs { get; init; } = 4;
    public long? AdapterLuid { get; init; }
    public string SenderName { get; init; } = SpoutOutput.DefaultSenderName;
    public bool SpoutEnabled { get; init; }
    public bool TestCardEnabled { get; init; }
    public OutputTrace Trace { get; init; } = OutputTrace.Disabled;
    public Action<PreviewFrame>? PreviewFrameReady { get; init; }

    public static bool TestCardRequested()
    {
        string? value = Environment.GetEnvironmentVariable(TestCardEnvironmentVariable);
        return value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
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
    private readonly LatestPool pool = new(3);
    private readonly ScanoutTracker scanout = new(16);
    private readonly ScheduleOffset scheduleOffset = new();
    private readonly ComposeAlignGate align;
    private readonly SnapshotInputMailbox snapshotInput = new();
    private readonly TimelineOutputMailbox timelineInput = new();
    private readonly UploadSlot?[] uploadSlots = new UploadSlot?[4];
    private MpvSnapshotSource<int>? mpvSource;
    private ComposeLayer? layer;
    private ComposeLeadController? composeLead;
    private TimelineOutputState? lastTimelineState;
    private int sourceGeneration = -1;
    private GStreamerSource? gstSource;
    private long lastGstSequence = -1;
    private int lastGstGeneration = -1;
    // 配信トレース（問題 H）。shim の QPC イベントを events.jsonl へ写す。
    private IGstNativeApi? gstNative;
    private IntPtr gstPlayer;
    private long gstDeliveryDrainQpc;
    private GstNative.TcsDeliveryEvent[]? gstDeliveryBuffer;

    // GStreamer shim へのデバイス Adopt 用（GPU worker が初期化時に確定する）。
    private readonly ManualResetEventSlim deviceReady = new(false);
    private IntPtr devicePointer;

    // GPU worker のみが触る状態。
    private GpuDevice? gpu;
    private ShaderPipeline? shaders;
    private readonly List<Surface> surfaces = new();
    private SharedFence? sharedFence;
    private SwapchainTarget? target;
    private VblankDisplayGate? vblank;
    private VblankWaitTimer? gpuLoopTimer;
    private VblankWaitTimer? vblankTimer;
    private ID3D11Texture2D? previewTexture;
    private ID3D11RenderTargetView? previewTarget;
    private ID3D11Texture2D? previewStaging;
    private TickSchedule? composeSchedule;
    private long originQpc;
    private long nextImageId;
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

    // Spout worker（GPU worker が起動・停止を管理）。
    private Thread? spoutThread;
    private CancellationTokenSource? spoutStop;
    private ManualResetEventSlim? spoutReady;
    private long spoutCpuStartTicks;

    private readonly PreviewHandoff previewHandoff = new(3);

    public OutputEngine(OutputEngineSettings settings)
    {
        this.settings = settings;
        testCard = settings.TestCardEnabled;
        spoutRunning = settings.SpoutEnabled;
        spoutEverEnabled = settings.SpoutEnabled;
        align = new ComposeAlignGate(settings.ComposeLeadMs, Stopwatch.Frequency);
    }

    public bool Faulted => Volatile.Read(ref faulted) != 0;
    public string? FirstFault => firstFault;

    /// <summary>UI スレッド。Retain 済みの mpv スナップショットを GPU worker へ渡す（所有権も移す）。</summary>
    public void SubmitFrame(RenderedFrameSnapshot frame, int generation, double positionSeconds)
        => snapshotInput.Publish(frame, generation, positionSeconds);

    /// <summary>UI スレッド。タイムライン状態（ギャップ・カード・世代・位置）を GPU worker へ渡す。</summary>
    public void SubmitTimelineState(TimelineOutputState state)
        => timelineInput.Publish(state);

    /// <summary>GStreamer shim に Adopt させる ID3D11Device が確定するまで待つ（起動時のみ）。</summary>
    public bool WaitForDevice(TimeSpan timeout) => deviceReady.Wait(timeout);

    /// <summary>Adopt 済みデバイスポインタ（未初期化なら IntPtr.Zero）。</summary>
    public IntPtr DevicePointer => Volatile.Read(ref devicePointer);

    /// <summary>
    /// UI スレッド。GStreamer プレイヤーをソースとして接続する（PlayerBackend=Gstreamer かつ Gpu 出力時）。
    /// 以降、合成 tick は CPU アップロードではなく shim のリースを取得してエンジン slot へ GPU コピーする。
    /// </summary>
    public void AttachGStreamerSource(IntPtr player, TimecodeSyncPlayer.Gst.IGstNativeApi native)
        => Enqueue(() =>
        {
            if (player == IntPtr.Zero || gpu == null)
            {
                Fault("OutputEngine: GStreamer プレイヤーハンドルがありません");
                return;
            }
            gstSource?.Dispose();
            gstSource = new GStreamerSource(new GstNativeLeasePlayer(native, player), gpu.Luid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            gstNative = native;
            gstPlayer = player;
            lastGstSequence = -1;
            lastGstGeneration = -1;
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
        displayEverAttached = true;
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

    private void Run()
    {
        using var process = Process.GetCurrentProcess();
        long cpuStartQpc = Stopwatch.GetTimestamp();
        TimeSpan cpuStart = process.TotalProcessorTime;
        // 失敗時もトレースの原点が壊れないよう、初期化前に記録する（ループ原点は初期化後に確定する）。
        settings.Trace.OriginQpc = cpuStartQpc;
        try
        {
            gpu = new GpuDevice(settings.AdapterLuid, Fault, fenceSync: true);
            // GStreamer shim の Adopt 用に、デバイス確定を起動側へ知らせる。
            Volatile.Write(ref devicePointer, gpu.Device.NativePointer);
            deviceReady.Set();
            shaders = new ShaderPipeline(gpu);
            for (int i = 0; i < pool.Capacity; i++)
                surfaces.Add(new Surface(gpu, gpu.Texture(settings.CanvasWidth, settings.CanvasHeight, SourceSharing.FenceNt), true, SourceSharing.FenceNt));
            sharedFence = new SharedFence(gpu);
            originQpc = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
            settings.Trace.OriginQpc = originQpc;
            gpuLoopTimer = new VblankWaitTimer();
            gpuLoopTimerHighResolution = gpuLoopTimer.HighResolution;
            CreatePreviewTargets(gpu);
            var slotIndices = new List<int> { 0, 1, 2, 3 };
            mpvSource = new MpvSnapshotSource<int>(slotIndices, UploadToSlot,
                index => new SourceImageDescription(uploadSlots[index]?.Surface?.Texture,
                    uploadSlots[index]?.Width ?? 0, uploadSlots[index]?.Height ?? 0, SourceImageFormat.Bgra8),
                "mpv-bgra", gpu.Luid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            layer = new ComposeLayer(gpu, shaders, CanvasSettings.Default);
            composeLead = new ComposeLeadController(Stopwatch.Frequency, settings.ComposeLeadMs);
            if (spoutRunning) StartSpoutWorker();
            Log.Information("OutputEngine: 初期化完了 canvas={W}x{H} adapterLuid={Luid}",
                settings.CanvasWidth, settings.CanvasHeight, gpu.Luid);
            Loop();
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
                settings.CanvasWidth, settings.CanvasHeight, settings.PresentMarginMs, settings.ComposeLeadMs,
                settings.SenderName, cpuSeconds, cpuStartQpc, cpuEndQpc, vblankTimerHighResolution, gpuLoopTimerHighResolution, spoutLoopTimerHighResolution),
                pool, scanout, TryDiagnostics());
            gpuDone?.TrySetResult();
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
            long now = Stopwatch.GetTimestamp();
            long due = schedule.DueQpc;
            if (vblank != null && VblankIdle(lastScheduled, now, due)) continue;
            if (now < due)
            {
                // 空き時間にリングへアップロードする（合成 tick は取得だけにする）。
                UploadPendingSnapshot();
                now = Stopwatch.GetTimestamp();
                if (now < due)
                {
                    if (LoopIdleWait.UseTimer(now, due, frequency))
                        gpuLoopTimer!.WaitUntilOrStopOrSignal(stop.Token.WaitHandle, snapshotInput.ReadyHandle, now, due, frequency);
                    else Thread.Yield();
                }
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
        long latestId = pool.LatestId;
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
            if (HoldVblankReadiness(target, slot))
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
        int slot = pool.TryBeginWrite();
        if (slot < 0) { settings.Trace.Add("skip", "GPU", scheduled, detail: "compose.noFreeSlot", value: 1); return; }
        var surface = surfaces[slot];

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
            SyncGStreamerGeneration();
            var gst = AcquireGStreamer(position);
            status = gst.Status;
            lease = gst.Lease;
            acquired = gst.Image;
            settings.Trace.Add("source.acquire", "GPU", scheduled,
                new ImageStamp(gst.Stamp.Sequence, gst.Stamp.DecodedQpc), status.ToString(),
                (long)Math.Round(position * 1_000_000));
            if (status != SourceStatus.Ready && (effective?.Gap ?? OutputGapMode.None) == OutputGapMode.None)
                settings.Trace.Add("skip", "GPU", scheduled, detail: "compose.sourceNotReady", value: 1);
        }
        else
        {
            // mpv: 完了したアップロードだけを公開してからリングから取得する。
            mpvSource!.PollUploads();
            status = mpvSource.TryAcquire(Math.Max(generation, 0), position, out lease);
            ImageStamp acquiredStamp = lease != null ? new ImageStamp(lease.Stamp.Sequence, lease.Stamp.DecodedQpc) : default;
            settings.Trace.Add("source.acquire", "GPU", scheduled, acquiredStamp, status.ToString(),
                (long)Math.Round(position * 1_000_000));
            if (status != SourceStatus.Ready && (effective?.Gap ?? OutputGapMode.None) == OutputGapMode.None)
                settings.Trace.Add("skip", "GPU", scheduled, detail: "compose.sourceNotReady", value: 1);
            if (lease != null)
            {
                var uploadSlot = uploadSlots[mpvSource.SlotOf(lease)];
                if (uploadSlot?.Surface != null)
                    acquired = new LayerImage(uploadSlot.Surface.View, uploadSlot.Surface.Texture.NativePointer,
                        uploadSlot.Width, uploadSlot.Height, lease, null);
                else { lease.Dispose(); lease = null; }
            }
        }

        bool writing = true, inFlight = false, retained = false;
        try
        {
            var stamp = new ImageStamp(++nextImageId, Stopwatch.GetTimestamp());
            long composeStartedQpc = Stopwatch.GetTimestamp();
            settings.Trace.Add("compose.start", "GPU", scheduled, stamp);
            if (lease != null) { lease.BeginGpuUse(); inFlight = true; }
            retained = layer!.Compose(surface,
                effective?.Gap ?? OutputGapMode.None,
                effective?.Clip ?? new ClipPlacement(null),
                effective?.TestCardEnabled ?? testCard,
                stamp, originQpc, acquired);
            sharedFence!.Signal(gpu!, stamp.Id); // フェンス値＝画像 ID。Spout 側は GPU キューで待つ。
            gpu!.Fence.Wait("compose.source");
            long composeCompletedQpc = Stopwatch.GetTimestamp();
            if (inFlight) { lease!.CompleteGpuUse(); inFlight = false; }
            if (!retained && acquired != null) { acquired.Value.Release(); acquired = null; lease = null; }
            UpdateComposeLead(composeCompletedQpc - composeStartedQpc, composeCompletedQpc);
            settings.Trace.Add("compose.complete", "GPU", scheduled, stamp);
            settings.Trace.Add("compose.publish", "GPU", scheduled, stamp);
            pool.Publish(slot, stamp, true);
            writing = false;
            settings.Trace.Record(new("compose.visible", "GPU", Stopwatch.GetTimestamp(), scheduled, stamp.Id, stamp.GeneratedQpc));
        }
        finally
        {
            if (inFlight) lease!.CompleteGpuUse();
            if (!retained && acquired != null) acquired.Value.Release();
            // GStreamer のリースは shim が最新へ進めるよう毎 tick 返す（描画テクスチャは AddRef 済み）。
            if (gstSource != null) lease?.Dispose();
            if (writing) { try { gpu!.Fence.Wait("compose.drain"); } finally { pool.AbortWrite(slot, true); } }
        }

        if (stop.IsCancellationRequested) return;
        if (target != null && vblank != null && !vblank.HasScanout)
        {
            long now = Stopwatch.GetTimestamp();
            if (vblank.Decide(scheduled, now, nextScheduled, pool.LatestId, stop.IsCancellationRequested).Step == VblankStep.Bootstrap)
            {
                settings.Trace.Add("display.vblank.bootstrap", "GPU", scheduled, value: 1);
                if (HoldVblankReadiness(target, scheduled)) Present(scheduled, nextScheduled);
            }
        }
        if (target != null) ObserveScanout();
        UpdatePreview(Stopwatch.GetTimestamp());
    }

    private void UploadPendingSnapshot()
    {
        if (mpvSource == null) return;
        if (!snapshotInput.TryTake(out var pending, out int generation, out double position) || pending == null) return;
        EnsureSourceGeneration(generation);
        mpvSource.TryUpload(pending, generation, position);
        // 完了済みのアップロードだけをリングへ公開する（合成のフェンス待ちに含めない）。
        mpvSource.PollUploads();
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
            return mpvSource?.Diagnostics;
        }
        catch (Exception) { return null; }
    }

    private void EnsureSourceGeneration(int generation)
    {
        if (generation < 0 || generation == sourceGeneration) return;
        sourceGeneration = generation;
        mpvSource!.SetGeneration(generation);
        // GStreamer の世代は shim 側の値を観測して対応付ける（SyncGStreamerGeneration）。
        layer!.ClearFreeze();
    }

    private readonly record struct GstFrameAcquire(SourceStatus Status, ISourceImageLease? Lease, LayerImage? Image, SourceImageStamp Stamp);

    // GStreamer の世代は shim 側の値（load/seek で進む）を観測して対応付ける。
    // 世代が変わったら Held を手放し、古い世代の画像を返さない。
    private void SyncGStreamerGeneration()
    {
        if (gstSource == null) return;
        int shimGeneration = gstSource.Generation;
        if (shimGeneration == lastGstGeneration) return;
        lastGstGeneration = shimGeneration;
        layer?.ClearHeld();
        lastGstSequence = -1;
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
                    GstNative.TcsDeliveryEvent e = buffer[i];
                    settings.Trace.Record(new("gst.delivery", "GST", (long)e.Qpc, ImageId: (long)e.Seq,
                        Detail: $"{e.PtsNs}:{e.RunningNs}:{e.Flags}", Value: e.CallbackUs));
                }
                if (count < buffer.Length) return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OutputEngine: 配信トレースの取得に失敗");
        }
    }

    // GStreamer shim の最新リースを取得し、借用テクスチャの SRV を作って直接描画できる形で返す。
    // shim は「リース保持中は同じ画像を返す」ため、リースは compose 後に毎回返す（呼び出し側が Dispose）。
    // 描画に使うテクスチャは AddRef 付きの Surface として LayerImage が所有する。
    private GstFrameAcquire AcquireGStreamer(double position)
    {
        int generation = gstSource!.Generation;
        var status = gstSource.TryAcquire(generation, position, out var lease);
        if (status != SourceStatus.Ready || lease == null) return new(status, null, null, default);
        var stamp = lease.Stamp;
        if (stamp.Sequence == lastGstSequence) return new(status, lease, null, stamp);
        lastGstSequence = stamp.Sequence;
        var texture = ((GStreamerSource.Lease)lease).OpenTexture();
        if (texture == null) return new(status, lease, null, stamp);
        Surface surface;
        try { surface = new Surface(gpu!, texture, false, SourceSharing.None); }
        catch { texture.Dispose(); throw; }
        // Lease は LayerImage に持たせない（毎 tick 返す）。テクスチャ/SRV は AddRef 済みで保持される。
        var image = new LayerImage(surface.View, surface.Texture.NativePointer, lease.Width, lease.Height, null, surface);
        return new(status, lease, image, stamp);
    }

    // GPU worker 専用: 空き slot のテクスチャを必要サイズへ作り直してアップロードする。
    // slot はリングから外れ lease も無いときだけ渡ってくるため、作り直しは安全。
    // コピー完了は専用の EVENT クエリで確認し、完了まではリングへ公開しない。
    private IUploadCompletion UploadToSlot(int index, byte[] pixels, int width, int height)
    {
        var slot = uploadSlots[index] ??= new UploadSlot();
        if (slot.Surface == null || slot.Width != width || slot.Height != height)
        {
            slot.Surface?.Dispose();
            slot.Surface = new Surface(gpu!, gpu!.Texture(width, height, SourceSharing.None), false, SourceSharing.None);
            slot.Width = width;
            slot.Height = height;
        }
        var context = gpu!.Context;
        context.UpdateSubresource<byte>(pixels.AsSpan(0, width * height * 4), slot.Surface.Texture, 0, (uint)(width * 4), 0);
        var query = gpu.Device.CreateQuery(new QueryDescription(QueryType.Event));
        context.End(query);
        context.Flush();
        return new UploadCompletion(context, query);
    }

    private sealed class UploadCompletion(ID3D11DeviceContext context, ID3D11Query query) : IUploadCompletion
    {
        private bool disposed;

        public unsafe bool TryComplete()
        {
            int done = 0;
            int hr = context.GetData(query, (IntPtr)(&done), 4, AsyncGetDataFlags.DoNotFlush).Code;
            return hr == 0 && done != 0;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            query.Dispose();
        }
    }

    private sealed class UploadSlot
    {
        public Surface? Surface;
        public int Width;
        public int Height;
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
            var lease = pool.AcquireLatest();
            if (lease == null) return;
            try
            {
                lease.BeginGpuUse();
                bool inFlight = true;
                try
                {
                    shaders!.Display(surfaces[lease.Slot], previewTarget!, 960, 540, settings.CanvasWidth, settings.CanvasHeight);
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

    private bool HoldVblankReadiness(SwapchainTarget swapchain, long slot)
    {
        if (swapchain.Readiness.PermissionHeld) return true;
        if (swapchain.WaitReady(0)) { swapchain.Readiness.GrantFromNotification(); return true; }
        settings.Trace.Add("skip", "GPU", slot, detail: "display.vblank.notReady", value: 1);
        vblank!.Defer();
        return false;
    }

    private void Present(long scheduled, long nextScheduled)
    {
        long deadline = vblank?.PresentDeadline(nextScheduled, long.MaxValue) ?? nextScheduled;
        vblank?.BeginAttempt(scheduled);
        long selectStarted = Stopwatch.GetTimestamp();
        using var lease = pool.AcquireLatest();
        long selectEnded = Stopwatch.GetTimestamp();
        settings.Trace.Record(new("display.select.start", "GPU", selectStarted, scheduled, lease?.Stamp.Id ?? 0, lease?.Stamp.GeneratedQpc ?? 0, DeadlineQpc: deadline));
        ImageStamp selected = lease?.Stamp ?? default;
        settings.Trace.Record(new("display.select.end", "GPU", selectEnded, scheduled, selected.Id, selected.GeneratedQpc,
            lease == null ? "none" : "latest", DeadlineQpc: deadline));
        if (lease == null) return;
        string? stale = vblank?.SkipReason(lease.Stamp.Id);
        if (stale != null) { settings.Trace.Add("skip", "GPU", scheduled, lease.Stamp, stale, 1); return; }
        settings.Trace.Record(new("display.fence.wait", "GPU", Stopwatch.GetTimestamp(), scheduled, lease.Stamp.Id, lease.Stamp.GeneratedQpc,
            Detail: lease.Slot.ToString(), Value: lease.Stamp.Id));
        Surface source = surfaces[lease.Slot];
        bool inFlight = false;
        try
        {
            long drawStarted = Stopwatch.GetTimestamp();
            lease.BeginGpuUse(); inFlight = true;
            try { shaders!.Display(source, target!.Target, target.Width, target.Height, settings.CanvasWidth, settings.CanvasHeight); }
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
            sendGpu = new GpuDevice(gpu!.Luid, Fault, fenceSync: true);
            foreach (var surface in surfaces)
                opened.Add(new Surface(sendGpu, sendGpu.Device1.OpenSharedResource1<ID3D11Texture2D>(surface.Handle), false, SourceSharing.None));
            fenceReader = new SharedFenceReader(sendGpu, sharedFence!.Open(sendGpu));
            sender = new SpoutSender(sendGpu, settings.Trace, "Spout", settings.SenderName,
                settings.CanvasWidth, settings.CanvasHeight, MutexWaitPolicy.MaxWaitMs, fenceReader);
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
                sender.Update(reads, pool, tick.Scheduled, token);
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
        mpvSource?.DrainPendingForStop();
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
        try { mpvSource?.TryDispose(); } catch (Exception e) { Fault("GPU.source: " + e); }
        DisposeOwned(mpvSource, "GPU.source"); mpvSource = null;
        foreach (var slot in uploadSlots)
            DisposeOwned(slot?.Surface, "GPU.uploadSlot");
        snapshotInput.Dispose();
        foreach (var surface in surfaces)
        {
            try { surface.CloseSharedHandle(); } catch (Exception e) { Fault("GPU.surfaceHandle: " + e); }
            DisposeOwned(surface, "GPU.surface");
        }
        surfaces.Clear();
        DisposeOwned(sharedFence, "GPU.fence"); sharedFence = null;
        DisposeOwned(shaders, "GPU.shaders"); shaders = null;
        DisposeOwned(previewTarget, "GPU.previewTarget"); previewTarget = null;
        DisposeOwned(previewTexture, "GPU.previewTexture"); previewTexture = null;
        DisposeOwned(previewStaging, "GPU.previewStaging"); previewStaging = null;
        DisposeOwned(gpu, "GPU.device"); gpu = null;
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
