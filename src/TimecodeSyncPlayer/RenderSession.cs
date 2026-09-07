using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Serilog;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

/// <summary>
/// Native callbacks drain update/render on one independent worker, even while UI property
/// calls block. A bounded mailbox transfers copied frames to UI-owned WPF/Spout/freeze buffers.
/// Stop waits only for native work, never the UI publication continuation.
/// </summary>
internal sealed class RenderSession : IDisposable
{
    private readonly IMpvRenderApi _api;
    private readonly ISpoutOutput _spoutOutput;
    private readonly PlaybackPerformanceStats _stats;
    private readonly Func<GapState> _getGapState;
    private readonly Func<GapBehavior> _getGapBehavior;
    private readonly Func<bool> _isGapFreezeConfirmed;
    private readonly Action<Action> _scheduleUpdate;
    private readonly PixelBufferManager _buffers = new();
    private readonly PixelBufferManager _nativeBuffers = new();
    private readonly RenderThreadExecutor _thread = new();
    private readonly RenderUpdateGeneration _generation = new();
    private readonly IRenderUpdateScheduler _scheduler = new RenderUpdateScheduler();
    private readonly RenderUpdateScheduler _nativeScheduler = new();
    private readonly LatestRenderedFrameMailbox _mailbox = new();
    private readonly object _submissionSync = new();
    private readonly AsyncLocal<PreparedFrame?> _preparedFrame = new();
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
    private long _frameSequence;
    // UI-owned: a callback lease can resume after a newer explicit redraw/capture.
    private long _lastAppliedSequence;
    private int _pendingHasFrame;
    private int _width;
    private int _height;
    private int _lastFrameWidth;
    private int _lastFrameHeight;
    private volatile bool _stopped;
    private bool _disposed;

    private sealed record PreparedFrame(int Generation, bool HasFrame, RenderedFrameSnapshot? Snapshot);

    public RenderSession(IMpvRenderApi api, ISpoutOutput spoutOutput, PlaybackPerformanceStats stats,
        Func<GapState> getGapState, Func<GapBehavior> getGapBehavior, Action<Action> scheduleUpdate,
        Func<bool>? isGapFreezeConfirmed = null)
    {
        _api = api;
        _spoutOutput = spoutOutput;
        _stats = stats;
        _getGapState = getGapState;
        _getGapBehavior = getGapBehavior;
        _isGapFreezeConfirmed = isGapFreezeConfirmed ?? (() => true);
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
            ensurePixelBuffer: (width, height) => _nativeBuffers.EnsurePixelBuffer(width, height),
            buildRenderParameters: (width, height) => RenderFrameParameterBuilder.Build(_nativeBuffers, _parameters!, _api, width, height),
            renderFrame: executor.Render,
            decidePublish: RenderFramePublishPolicy.Decide,
            logRenderFailure: rc => Log.Debug("mpv_render_context_render: rc={Rc}", rc));
    }

    public event Action<WriteableBitmap>? BitmapChanged;
    public Func<int, bool, Task>? FrameUpdate { get; set; }
    public int Width { get => Volatile.Read(ref _width); set => Volatile.Write(ref _width, value); }
    public int Height { get => Volatile.Read(ref _height); set => Volatile.Write(ref _height, value); }
    public int CaptureGeneration() => _generation.Capture();
    public bool IsCurrent(int generation) => !_stopped && _generation.IsCurrent(generation);
    public void Invalidate()
    {
        _generation.Advance();
        _mailbox.Clear();
    }
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
        _updateCallback = callbackContext =>
        {
            // No ordinary mpv API calls, UI wait, or rendering inside the native callback.
            lock (_submissionSync)
            {
                if (_stopped || !_nativeScheduler.RequestDispatch()) return;
                _ = DrainNativeUpdatesAsync();
            }
        };
        _thread.InvokeAsync(() =>
        {
            _parameters = new MpvRenderNative.MpvRenderParam[5];
            new StartupBufferInitializer(_nativeBuffers).Initialize("bgr0");
            _api.RenderContextSetUpdateCallback(_context, _updateCallback, IntPtr.Zero);
        })
            .GetAwaiter().GetResult();
        Log.Information("mpv SW レンダーコンテキスト作成完了");
        return true;
    }

    // Kept as separate startup phases to preserve context / Spout / bitmap / timer / buffer order.
    public void AllocateParameters() { } // Native parameters must exist before callback registration.
    public void InitializeFrameRenderer()
    {
        _renderer = new FrameRenderer(_buffers, _spoutOutput);
        _renderer.BitmapChanged += bitmap => BitmapChanged?.Invoke(bitmap);
    }
    public void InitializeStartupBuffer() => new StartupBufferInitializer(_buffers).Initialize("bgr0");
    public void ClearGapFreezeFrame() => _buffers.ClearGapFreezeFrame();
    // Called inside RenderFrameAsync's gated afterFrameProcessed callback, on the UI thread.
    public void CaptureGapFreezeFrame() => _buffers.CopyFrozenToGapFreezeFrame(_lastFrameWidth, _lastFrameHeight);

    private async Task DrainNativeUpdatesAsync()
    {
        try
        {
            await _thread.InvokeAsync(() =>
            {
                try
                {
                    if (_stopped) return;
                    PreparedFrame update = ReadNativeUpdate();
                    if (IsCurrent(update.Generation))
                    {
                        if (update.Snapshot != null) _mailbox.Publish(update.Snapshot);
                        if (update.HasFrame) Interlocked.Exchange(ref _pendingHasFrame, 1);
                        if (_scheduler.RequestDispatch()) _scheduleUpdate(OnRenderUpdate);
                    }
                    else update.Snapshot?.Dispose();
                }
                finally
                {
                    // A playing frame can signal its successor before render returns.
                    // Yield the executor queue after each frame so explicit redraws,
                    // capture and shutdown cannot be starved by a perpetual drain loop.
                    if (_nativeScheduler.CompleteDispatch() && !_stopped)
                        _ = DrainNativeUpdatesAsync();
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Native render update failed");
        }
    }

    private PreparedFrame ReadNativeUpdate()
    {
        int generation = CaptureGeneration();
        ulong flags = _api.RenderContextUpdate(_context);
        bool hasFrame = (flags & _api.MpvRenderUpdateFrame) != 0;
        // Even stale updates and unknown dimensions must consume native FRAME work.
        RenderedFrameSnapshot? frame = hasFrame ? RenderNativeSnapshot(generation) : null;
        return new PreparedFrame(generation, hasFrame, frame);
    }

    private RenderedFrameSnapshot? RenderNativeSnapshot(int generation)
    {
        if (_stopped || _context == IntPtr.Zero || _parameters == null) return null;
        var size = RenderFrameSizePolicy.Decide(Width, Height, 16);
        if (!size.ShouldRender)
            size = new RenderFrameSizeDecision(16, 16, HasDisplayableVideoSize: false);
        RenderFrameWorkerResult result = _worker.Execute(size);
        if (!result.ShouldPublish || !IsCurrent(generation)) return null;
        return RenderedFrameSnapshot.Copy(_nativeBuffers.PixelBuffer!, result.Width, result.Height,
            generation, Interlocked.Increment(ref _frameSequence), result.RenderMs);
    }

    private async void OnRenderUpdate()
    {
        try
        {
            await AsyncOperationExceptionBoundary.RunAsync(
                () => ProcessPreparedUpdateAsync(TakePendingUpdate(),
                    FrameUpdate ?? ((_, _) => Task.CompletedTask)),
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
        if (_context == IntPtr.Zero || _stopped) return;
        PreparedFrame update = await _thread.InvokeAsync(ReadNativeUpdate);
        await ProcessPreparedUpdateAsync(update, processFrame);
    }

    private PreparedFrame TakePendingUpdate()
    {
        var frame = _mailbox.Take();
        bool hasFrame = Interlocked.Exchange(ref _pendingHasFrame, 0) != 0 || frame != null;
        return new PreparedFrame(frame?.Generation ?? CaptureGeneration(), hasFrame, frame);
    }

    private async Task ProcessPreparedUpdateAsync(PreparedFrame update, Func<int, bool, Task> processFrame)
    {
        using var snapshot = update.Snapshot;
        int generation = snapshot?.Generation ?? update.Generation;
        if (!IsCurrent(generation)) return;
        var previous = _preparedFrame.Value;
        _preparedFrame.Value = update;
        try
        {
            _stats.RecordRenderUpdate(update.HasFrame);
            await processFrame(generation, update.HasFrame);
        }
        finally { _preparedFrame.Value = previous; }
    }

    public Task RenderFrameAsync(int generation, Action? afterFrameProcessed = null)
    {
        // Capture the callback's lease before any await. It remains stable while later
        // native frames replace the mailbox; unrelated UI calls cannot steal that lease.
        PreparedFrame? prepared = _preparedFrame.Value;
        return _gate.RunAsync(async () =>
        {
            if (!IsCurrent(generation) || _context == IntPtr.Zero) return;
            if (prepared != null)
            {
                if (prepared.Snapshot != null && prepared.Snapshot.Generation == generation)
                    PublishSnapshot(prepared.Snapshot, afterFrameProcessed);
                return;
            }
            using var frame = await _thread.InvokeAsync(() => RenderNativeSnapshot(generation));
            if (frame != null && IsCurrent(generation)) PublishSnapshot(frame, afterFrameProcessed);
        });
    }

    private void PublishSnapshot(RenderedFrameSnapshot frame, Action? afterFrameProcessed)
    {
        GapRenderFrameDecision decision = GetGapRenderDecision();
        if (decision == GapRenderFrameDecision.Hold) return;
        if (!IsCurrent(frame.Generation) || frame.Sequence < _lastAppliedSequence ||
            !_buffers.CopySnapshotToPixelBuffer(frame)) return;
        _lastAppliedSequence = frame.Sequence;
        _lastFrameWidth = frame.Width;
        _lastFrameHeight = frame.Height;
        GapState state = _getGapState();
        RenderFramePublicationDispatcher.Execute(
            decision,
            publishNormalFrame: () => _publishPipeline.Publish(_buffers.PixelPtr, frame.Width, frame.Height,
                frame.RenderMs, _spoutOutput.IsEnabled, state),
            captureWithoutPublishing: () => _freezeCopier.CopyIfNeeded(state, frame.Width, frame.Height),
            afterFrameProcessed);
    }

    /// <summary>Redraw only after the owner verifies paused seek completion and media identity.</summary>
    public async Task<bool> TryCaptureGapFreezeFrameAsync(int generation, Func<bool> isAttemptCurrent)
    {
        ArgumentNullException.ThrowIfNull(isAttemptCurrent);
        bool copied = false;
        await _gate.RunAsync(async () =>
        {
            if (!IsCurrent(generation) || !isAttemptCurrent() || _context == IntPtr.Zero) return;
            using var frame = await _thread.InvokeAsync(() => RenderNativeSnapshot(generation));
            if (frame == null || !IsCurrent(generation) || !isAttemptCurrent()) return;
            if (!_buffers.CopySnapshotToPixelBuffer(frame)) return;
            _buffers.EnsureFrozenFrameBuffer(frame.Width, frame.Height);
            copied = _buffers.TryCopyToFrozenFrame(frame.Width, frame.Height) &&
                _buffers.TryCopyFrozenToGapFreezeFrame(frame.Width, frame.Height);
            if (copied) _lastAppliedSequence = Math.Max(_lastAppliedSequence, frame.Sequence);
        });
        return copied;
    }

    public GapRenderFrameDecision GetGapRenderDecision() => GapRenderFramePolicy.Decide(
        _getGapState(), _getGapBehavior(),
        _buffers.CachedGapFreezeFrameBuffer != null &&
        _buffers.CachedGapFreezeFrameWidth > 0 && _buffers.CachedGapFreezeFrameHeight > 0 &&
        _isGapFreezeConfirmed(), Width, Height);

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

    /// <summary>Disable callbacks/publication and wait only for native work, never UI continuations.</summary>
    public void Stop()
    {
        Task barrier;
        lock (_submissionSync)
        {
            if (_stopped) return;
            _stopped = true;
            barrier = _thread.InvokeAsync(() => { });
        }
        // Every queued native operation precedes this barrier. No UI continuation is awaited.
        barrier.GetAwaiter().GetResult();
        _mailbox.Dispose();
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
        try { _nativeBuffers.Dispose(); }
        catch (Exception ex) { errors.Add(ex); }
        _parameters = null;
        try { _thread.Dispose(); }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException("RenderSession resource cleanup failed", errors);
    }
}
