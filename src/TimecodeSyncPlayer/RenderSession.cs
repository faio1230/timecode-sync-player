using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Serilog;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

/// <summary>
/// Owns the native rendering lifetime and shared frame buffers. Public operations and
/// publication run on the UI thread; native create/update/render/free use one worker thread.
/// The owner must Stop before destroying mpv or Spout. Only the native worker is waited
/// synchronously: awaiting the UI-capturing publication pipeline at shutdown would deadlock.
/// </summary>
internal sealed class RenderSession : IDisposable
{
    private readonly IMpvRenderApi _api;
    private readonly ISpoutOutput _spoutOutput;
    private readonly PlaybackPerformanceStats _stats;
    private readonly Func<GapState> _getGapState;
    private readonly Func<GapBehavior> _getGapBehavior;
    private readonly Action<Action> _scheduleUpdate;
    private readonly PixelBufferManager _buffers = new();
    private readonly RenderThreadExecutor _thread = new();
    private readonly RenderUpdateGeneration _generation = new();
    private readonly IRenderUpdateScheduler _scheduler = new RenderUpdateScheduler();
    private readonly RenderFramePipelineGate _gate = new();
    private readonly RenderFrameDisplayUpdater _displayUpdater;
    private readonly RenderedFrameFreezeBufferCopier _freezeCopier;
    private readonly RenderFramePublishPipeline _publishPipeline;
    private readonly RenderFrameWorker _worker;
    private FrameRenderer _renderer = null!;
    private IntPtr _context;
    private MpvRenderNative.MpvRenderParam[]? _parameters;
    // Native code does not root delegates. Retain this until RenderContextFree has returned.
    private MpvRenderNative.MpvRenderUpdateFn? _updateCallback;
    private Task? _activeWorker;
    private volatile bool _stopped;
    private bool _disposed;

    public RenderSession(IMpvRenderApi api, ISpoutOutput spoutOutput, PlaybackPerformanceStats stats,
        Func<GapState> getGapState, Func<GapBehavior> getGapBehavior, Action<Action> scheduleUpdate)
    {
        _api = api;
        _spoutOutput = spoutOutput;
        _stats = stats;
        _getGapState = getGapState;
        _getGapBehavior = getGapBehavior;
        _scheduleUpdate = scheduleUpdate;
        _freezeCopier = new RenderedFrameFreezeBufferCopier(_buffers);
        var spoutPublisher = new SpoutFramePublisher(spoutOutput);
        var performanceRecorder = new RenderFramePerformanceRecorder(stats);
        _displayUpdater = new RenderFrameDisplayUpdater(
            (width, height) => _renderer.UpdateFromPixelBuffer(width, height),
            (width, height) => Log.Information("RenderFrame: first frame displayed {W}x{H}", width, height));
        _publishPipeline = new RenderFramePublishPipeline(
            (width, height) => _displayUpdater.Update(width, height),
            (pixels, width, height) => spoutPublisher.Publish(pixels, width, height),
            performanceRecorder.Record,
            (state, width, height) => _freezeCopier.CopyIfNeeded(state, width, height));
        var executor = new MpvRenderFrameExecutor(() => _api.RenderContextRender(_context, _parameters!));
        _worker = new RenderFrameWorker(
            ensurePixelBuffer: (width, height) => _buffers.EnsurePixelBuffer(width, height),
            buildRenderParameters: (width, height) => RenderFrameParameterBuilder.Build(_buffers, _parameters!, _api, width, height),
            renderFrame: executor.Render,
            decidePublish: RenderFramePublishPolicy.Decide,
            logRenderFailure: rc => Log.Debug("mpv_render_context_render: rc={Rc}", rc));
    }

    public event Action<WriteableBitmap>? BitmapChanged;
    public Func<int, bool, Task>? FrameUpdate { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int CaptureGeneration() => _generation.Capture();
    public bool IsCurrent(int generation) => !_stopped && _generation.IsCurrent(generation);
    public void Invalidate() => _generation.Advance();
    public void ResetDisplay() => _displayUpdater.Reset();
    public void ResetUpdateStats() => _scheduler.Reset();
    public RenderUpdateSchedulerStats ConsumeUpdateStats() => _scheduler.ConsumeStats();

    public bool Create(IntPtr mpv)
    {
        ObjectDisposedException.ThrowIf(_stopped, this);
        IntPtr sw = Marshal.StringToHGlobalAnsi(_api.MpvRenderApiTypeSw);
        var initParameters = RenderContextParameterBuilder.BuildSoftwareBackendParams(_api, sw);
        (int ReturnCode, IntPtr Context) created;
        try
        {
            created = _thread.InvokeAsync(() =>
            {
                int rc = _api.RenderContextCreate(out IntPtr context, mpv, initParameters);
                return (rc, context);
            }).GetAwaiter().GetResult();
        }
        finally { Marshal.FreeHGlobal(sw); }
        _context = created.Context;
        if (!RenderContextCreateResult.FromReturnCode(_context, created.ReturnCode).Success)
        {
            Log.Error("mpv_render_context_create 失敗: rc={Rc}", created.ReturnCode);
            return false;
        }
        _updateCallback = _ =>
        {
            if (!_stopped && _scheduler.RequestDispatch())
                _scheduleUpdate(OnRenderUpdate);
        };
        _thread.InvokeAsync(() => _api.RenderContextSetUpdateCallback(_context, _updateCallback, IntPtr.Zero))
            .GetAwaiter().GetResult();
        Log.Information("mpv SW レンダーコンテキスト作成完了");
        return true;
    }

    // Kept as separate startup phases to preserve context / Spout / bitmap / timer / buffer order.
    public void AllocateParameters() => _parameters = new MpvRenderNative.MpvRenderParam[5];
    public void InitializeFrameRenderer()
    {
        _renderer = new FrameRenderer(_buffers, _spoutOutput);
        _renderer.BitmapChanged += bitmap => BitmapChanged?.Invoke(bitmap);
    }
    public void InitializeStartupBuffer() => new StartupBufferInitializer(_buffers).Initialize("bgr0");
    public void ClearGapFreezeFrame() => _buffers.ClearGapFreezeFrame();
    // Called inside RenderFrameAsync's gated afterFrameProcessed callback, on the UI thread.
    public void CaptureGapFreezeFrame() => _buffers.CopyFrozenToGapFreezeFrame(Width, Height);

    private async void OnRenderUpdate()
    {
        try
        {
            await AsyncOperationExceptionBoundary.RunAsync(
                () => ProcessUpdateAsync(FrameUpdate ?? ((_, _) => Task.CompletedTask)),
                ex => Log.Error(ex, "Render update callback failed"));
        }
        finally
        {
            if (_scheduler.CompleteDispatch() && !_stopped && _context != IntPtr.Zero)
                _scheduleUpdate(OnRenderUpdate);
        }
    }

    public async Task ProcessUpdateAsync(Func<int, bool, Task> processFrame)
    {
        int generation = CaptureGeneration();
        if (_context == IntPtr.Zero || _stopped) return;
        ulong flags = await InvokeWorkerAsync(() => _api.RenderContextUpdate(_context));
        if (!IsCurrent(generation)) return;
        bool hasFrame = (flags & _api.MpvRenderUpdateFrame) != 0;
        _stats.RecordRenderUpdate(hasFrame);
        await processFrame(generation, hasFrame);
    }

    public Task RenderFrameAsync(int generation, Action? afterFrameProcessed = null) => _gate.RunAsync(async () =>
    {
        if (!IsCurrent(generation) || _context == IntPtr.Zero || _parameters == null) return;
        var size = RenderFrameSizePolicy.Decide(Width, Height, 16);
        RenderFrameWorkerResult result = await InvokeWorkerAsync(() => _worker.Execute(size));
        if (!IsCurrent(generation) || !result.ShouldPublish) return;
        GapState state = _getGapState();
        GapRenderFrameDecision decision = GetGapRenderDecision();
        RenderFramePublicationDispatcher.Execute(
            decision,
            publishNormalFrame: () => _publishPipeline.Publish(result.Pixels, result.Width, result.Height,
                result.RenderMs, _spoutOutput.IsEnabled, state),
            captureWithoutPublishing: () => _freezeCopier.CopyIfNeeded(state, result.Width, result.Height),
            afterFrameProcessed);
    });

    public GapRenderFrameDecision GetGapRenderDecision() => GapRenderFramePolicy.Decide(
        _getGapState(), _getGapBehavior(), _buffers.FrozenFrameBuffer != null, Width, Height);

    public Task RenderGapAsync(GapRenderFrameDecision expectedDecision)
    {
        var deferred = new DeferredGapFrameOperation(expectedDecision, GetGapRenderDecision, () =>
        {
            if (expectedDecision == GapRenderFrameDecision.Black) _renderer.RenderBlack(Width, Height);
            else if (expectedDecision == GapRenderFrameDecision.GapFreeze) _renderer.RenderGapFreeze(Width, Height);
        });
        return _gate.RunAsync(() =>
        {
            if (!_stopped) deferred.RunIfCurrent();
            return Task.CompletedTask;
        });
    }

    public async void QueueGapFrame(GapRenderFrameDecision expectedDecision)
    {
        try { await RenderGapAsync(expectedDecision); }
        catch (Exception ex) { Log.Error(ex, "Queued frame pipeline operation failed"); }
    }

    private async Task<T> InvokeWorkerAsync<T>(Func<T> operation)
    {
        Task<T> task = _thread.InvokeAsync(operation);
        _activeWorker = task;
        try { return await task; }
        finally { if (ReferenceEquals(_activeWorker, task)) _activeWorker = null; }
    }

    /// <summary>Disable callbacks/publication and wait only for native work, never UI continuations.</summary>
    public void Stop()
    {
        _stopped = true;
        RenderWorkerShutdownWaiter.Wait(_activeWorker,
            ex => Log.Warning(ex, "Active render worker failed during shutdown; continuing resource teardown"));
        _activeWorker = null;
    }

    /// <summary>Must succeed before mpv is destroyed. Called on the owning UI thread.</summary>
    public void FreeContext()
    {
        Stop();
        if (_context == IntPtr.Zero) return;
        _thread.InvokeAsync(() => _api.RenderContextFree(_context)).GetAwaiter().GetResult();
        _context = IntPtr.Zero;
        _updateCallback = null;
    }

    /// <summary>After FreeContext succeeds, release params/buffers and join the idle native thread.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        var errors = new List<Exception>();
        // On failure retain the thread, callback and pinned buffers alongside the live context.
        // FreeContext can be explicitly retried by a caller that knows the native failure is recoverable.
        try { FreeContext(); }
        catch (Exception ex) { throw new AggregateException("RenderSession context cleanup failed", ex); }
        _disposed = true;
        try { _buffers.Dispose(); }
        catch (Exception ex) { errors.Add(ex); }
        _parameters = null;
        try { _thread.Dispose(); }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException("RenderSession resource cleanup failed", errors);
    }
}
