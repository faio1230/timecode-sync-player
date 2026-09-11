using System.Runtime.InteropServices;
using System.Diagnostics;
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
    private readonly LatestRenderedFrameMailbox _mailbox;
    private readonly SyncAccuracyTrace _trace;
    private readonly long _traceSessionId;
    // Native-thread-only attempt identity includes renders which never publish.
    private long _traceAttemptId;
    private int _traceGeneration, _traceWidth, _traceHeight;
    private readonly object _submissionSync = new();
    private readonly AsyncLocal<PreparedFrame?> _preparedFrame = new();
    private readonly RenderFramePipelineGate _gate = new();
    private readonly RenderFrameDisplayUpdater _displayUpdater;
    private readonly RenderedFrameFreezeBufferCopier _freezeCopier;
    private readonly RenderFramePublishPipeline _publishPipeline;
    private readonly OutputFrameFactory _outputFrames;
    private readonly RenderFrameWorker _worker;
    private FrameRenderer _renderer = null!;
    private PreviewFramePresenter? _preview;
    private bool _fullscreenActive;
    private readonly Func<SyncAccuracyTrace, PreviewFramePresenter> _createPreview;
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
        Func<bool>? isGapFreezeConfirmed = null, SyncAccuracyTrace? accuracyTrace = null,
        Func<SyncAccuracyTrace, PreviewFramePresenter>? createPreview = null)
    {
        _api = api;
        _spoutOutput = spoutOutput;
        _stats = stats;
        _getGapState = getGapState;
        _getGapBehavior = getGapBehavior;
        _isGapFreezeConfirmed = isGapFreezeConfirmed ?? (() => true);
        _scheduleUpdate = scheduleUpdate;
        _trace = accuracyTrace ?? SyncAccuracyTrace.Current;
        _createPreview = createPreview ?? (trace => new PreviewFramePresenter(trace));
        _traceSessionId = _trace.AllocateRenderSessionId();
        _mailbox = new LatestRenderedFrameMailbox(_trace.IsEnabled
            ? (frame, reason) => TraceFrame(frame, "discard", reason) : null);
        _freezeCopier = new RenderedFrameFreezeBufferCopier(_buffers);
        _outputFrames = new OutputFrameFactory(_buffers);
        var spoutPublisher = new SpoutFramePublisher(spoutOutput);
        var performanceRecorder = new RenderFramePerformanceRecorder(stats);
        _displayUpdater = new RenderFrameDisplayUpdater(
            (pixels, width, height) => _renderer.UpdateFromPixels(pixels, width, height),
            (width, height) => Log.Information("RenderFrame: first frame displayed {W}x{H}", width, height));
        _publishPipeline = new RenderFramePublishPipeline(
            frame =>
            {
                if (frame.Kind == OutputFrameKind.Normal)
                    return _displayUpdater.Update(frame.PixelArray, frame.Width, frame.Height);
                _renderer.Update(frame);
                return 0;
            },
            (pixels, width, height) => spoutPublisher.Publish(pixels, width, height),
            performanceRecorder.Record,
            (pixels, state, width, height) => _freezeCopier.CopyIfNeeded(pixels, state, width, height),
            kind => _renderer.QueuePreviewFromCurrentBitmap(kind), () => _preview?.ResetPending(),
            (frame, send) => _displayUpdater.UpdateCombined(frame.PixelArray, frame.Width, frame.Height, send, _renderer.UpdateFromPixelsWithSpout));
        var executor = new MpvRenderFrameExecutor(RenderNativeFrame);
        _worker = new RenderFrameWorker(
            ensurePixelBuffer: (width, height) => _nativeBuffers.EnsurePixelBuffer(width, height),
            buildRenderParameters: (width, height) => RenderFrameParameterBuilder.Build(_nativeBuffers, _parameters!, _api, width, height),
            renderFrame: executor.Render,
            decidePublish: RenderFramePublishPolicy.Decide,
            logRenderFailure: rc => Log.Debug("mpv_render_context_render: rc={Rc}", rc));
    }

    public event Action<WriteableBitmap>? BitmapChanged;
    public event Action<WriteableBitmap>? PreviewBitmapChanged;

    /// <summary>
    /// Gpu backend 時のみ設定する。通常フレームを Retain して GPU 出力層へ渡す（所有権も移す）。
    /// CPU 側の表示・Spout 公開は行わない。Gap 中のフレームは TimelineOutputState 側で解釈する。
    /// </summary>
    internal Action<RenderedFrameSnapshot, int, double>? GpuFrameSink { get; set; }

    /// <summary>スナップショット公開時点の再生位置（秒）。取得できなければ null。</summary>
    internal Func<double?>? PositionSecondsProvider { get; set; }

    /// <summary>
    /// Gpu 合成層がソース（GStreamer リース等）を直接所有する場合に true。
    /// CPU へのフレームコピー（snapshot 生成）を行わず、UI の順序・ギャップ状態機械だけを回す。
    /// </summary>
    internal bool SuppressFrameSnapshots { get; set; }
    // The fullscreen window must start from this full-resolution image, even paused.
    public WriteableBitmap? CurrentExternalBitmap => _renderer?.CurrentBitmap;
    public void SetFullscreenActive(bool active)
    {
        if (_stopped) return;
        _fullscreenActive = active;
        _preview?.SetMaximumFramesPerSecond(active ? 10 : 30);
    }
    public Func<int, bool, Task>? FrameUpdate { get; set; }
    public int Width { get => Volatile.Read(ref _width); set => Volatile.Write(ref _width, value); }
    public int Height { get => Volatile.Read(ref _height); set => Volatile.Write(ref _height, value); }
    public int CaptureGeneration() => _generation.Capture();
    public bool IsCurrent(int generation) => !_stopped && _generation.IsCurrent(generation);
    public void Invalidate()
    {
        _generation.Advance();
        _mailbox.Clear();
        _preview?.ResetPending();
    }
    public void ResetDisplay()
    {
        _preview?.ResetPending();
        _displayUpdater.Reset();
    }
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
        _preview?.Dispose();
        _preview = _createPreview(_trace);
        _preview.SetMaximumFramesPerSecond(_fullscreenActive ? 10 : 30);
        _preview.BitmapChanged += bitmap => PreviewBitmapChanged?.Invoke(bitmap);
        _renderer = new FrameRenderer(_trace,
            (bitmap, kind) =>
            {
                if (!_stopped) _preview.QueueFrame(bitmap, kind);
            }, () => _preview?.ResetPending());
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
                        if (update.Snapshot != null)
                        {
                            _mailbox.Publish(update.Snapshot);
                            // Gpu backend: UI を経由せず mpv 専用スレッドから GPU worker へ直接渡す。
                            // 位置はここ（snapshot 生成側）で読み、UI での再読取はしない。
                            var sink = GpuFrameSink;
                            if (sink != null)
                                sink(update.Snapshot.Retain(), update.Snapshot.Generation, PositionSecondsProvider?.Invoke() ?? 0);
                        }
                        if (update.HasFrame) Interlocked.Exchange(ref _pendingHasFrame, 1);
                        if (_scheduler.RequestDispatch()) _scheduleUpdate(OnRenderUpdate);
                    }
                    else
                    {
                        if (update.Snapshot != null) TraceFrame(update.Snapshot, "discard", "stale-before-mailbox");
                        update.Snapshot?.Dispose();
                    }
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
        // Gpu 合成層がソースを所有する構成では、GPU/CPU コピーを行わず UI 更新だけを駆動する。
        if (SuppressFrameSnapshots) return null;
        var size = RenderFrameSizePolicy.Decide(Width, Height, 16);
        if (!size.ShouldRender)
            size = new RenderFrameSizeDecision(16, 16, HasDisplayableVideoSize: false);
        if (_trace.IsEnabled)
        {
            _traceAttemptId++;
            _traceGeneration = generation; _traceWidth = size.Width; _traceHeight = size.Height;
        }
        RenderFrameWorkerResult result = _worker.Execute(size);
        if (!result.ShouldPublish || !IsCurrent(generation))
        {
            if (_trace.IsEnabled)
            {
                long now = Stopwatch.GetTimestamp();
                _trace.RecordRenderStage(_traceSessionId, _traceAttemptId, generation, null, "discard",
                    !result.ShouldPublish ? "native-not-publishable" : "stale-after-native", now, now, size.Width, size.Height);
            }
            return null;
        }
        long sequence = Interlocked.Increment(ref _frameSequence);
        if (!_trace.IsEnabled)
            return RenderedFrameSnapshot.Copy(_nativeBuffers.PixelBuffer!, result.Width, result.Height, generation, sequence, result.RenderMs);
        long started = Stopwatch.GetTimestamp(); bool copied = false;
        try
        {
            var frame = RenderedFrameSnapshot.Copy(_nativeBuffers.PixelBuffer!, result.Width, result.Height,
                generation, sequence, result.RenderMs);
            copied = true;
            return frame;
        }
        finally
        {
            _trace.RecordRenderStage(_traceSessionId, _traceAttemptId, generation, sequence, "snapshot-copy",
                copied ? "ready" : "exception", started, Stopwatch.GetTimestamp(), result.Width, result.Height);
        }
    }

    private int RenderNativeFrame()
    {
        if (!_trace.IsEnabled) return _api.RenderContextRender(_context, _parameters!);
        long started = Stopwatch.GetTimestamp(); int? rc = null;
        try { rc = _api.RenderContextRender(_context, _parameters!); return rc.Value; }
        finally
        {
            // Entire native API duration, including its waits/conversion. Not decoder-only.
            _trace.RecordRenderStage(_traceSessionId, _traceAttemptId, _traceGeneration, null, "native-render",
                rc.HasValue ? "completed" : "exception", started, Stopwatch.GetTimestamp(), _traceWidth, _traceHeight, rc);
        }
    }

    private void TraceFrame(RenderedFrameSnapshot frame, string stage, string outcome)
    {
        if (!_trace.IsEnabled) return;
        long now = Stopwatch.GetTimestamp();
        _trace.RecordRenderStage(_traceSessionId, null, frame.Generation, frame.Sequence, stage, outcome,
            now, now, frame.Width, frame.Height);
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
        if (!IsCurrent(generation))
        {
            if (snapshot != null) TraceFrame(snapshot, "discard", "stale-prepared");
            return;
        }
        var previous = _preparedFrame.Value;
        _preparedFrame.Value = update;
        try
        {
            _stats.RecordRenderUpdate(update.HasFrame);
            await processFrame(generation, update.HasFrame);
        }
        finally
        {
            _preparedFrame.Value = previous;
            if (snapshot != null) TraceFrame(snapshot, "lease-release", "processed");
        }
    }

    public Task RenderFrameAsync(int generation, Action? afterFrameProcessed = null)
    {
        // Capture the callback's lease before any await. It remains stable while later
        // native frames replace the mailbox; unrelated UI calls cannot steal that lease.
        PreparedFrame? prepared = _preparedFrame.Value;
        return _gate.RunAsync(async () =>
        {
            if (!IsCurrent(generation) || _context == IntPtr.Zero)
            {
                if (prepared?.Snapshot != null) TraceFrame(prepared.Snapshot, "discard", "stale-before-ui-gate");
                return;
            }
            if (prepared != null)
            {
                if (prepared.Snapshot != null && prepared.Snapshot.Generation == generation)
                    PublishSnapshot(prepared.Snapshot, afterFrameProcessed);
                return;
            }
            using var frame = await _thread.InvokeAsync(() => RenderNativeSnapshot(generation));
            if (frame != null)
            {
                if (IsCurrent(generation)) PublishSnapshot(frame, afterFrameProcessed);
                else TraceFrame(frame, "discard", "stale-explicit-render");
            }
        });
    }

    private void PublishSnapshot(RenderedFrameSnapshot frame, Action? afterFrameProcessed)
    {
        GapRenderFrameDecision decision = GetGapRenderDecision();
        if (decision == GapRenderFrameDecision.Hold) { TraceFrame(frame, "discard", "gap-hold"); return; }
        if (!IsCurrent(frame.Generation)) { TraceFrame(frame, "discard", "stale-before-ui-source"); return; }
        if (frame.Sequence < _lastAppliedSequence) { TraceFrame(frame, "discard", "older-than-applied"); return; }
        BorrowSnapshotPixels(frame); // Preserve source-borrow diagnostics before output selection.
        _lastAppliedSequence = frame.Sequence;
        _lastFrameWidth = frame.Width;
        _lastFrameHeight = frame.Height;

        if (GpuFrameSink != null)
        {
            // Gpu backend: 画像は mpv 専用スレッド側で GPU worker へ渡済み。ここでは既存の
            // 世代・sequence 逆行防止と afterFrameProcessed だけを維持し、CPU の表示・Spout・Freeze コピーは行わない。
            if (decision != GapRenderFrameDecision.Hold)
                afterFrameProcessed?.Invoke();
            return;
        }

        GapState state = _getGapState();
        bool spoutEnabled = _spoutOutput.IsEnabled;
        bool combineBitmapAndSpout = _fullscreenActive && spoutEnabled;
        long started = _trace.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        bool succeeded = false;
        try
        {
            RenderFramePublicationDispatcher.Execute(
                decision,
                publishNormalFrame: () =>
                {
                    using var output = OutputFrame.FromSnapshot(frame);
                    _publishPipeline.Publish(output, spoutEnabled, state, _trace, _traceSessionId, combineBitmapAndSpout);
                },
                captureWithoutPublishing: () => CopyFreezeWithTrace(frame, state),
                afterFrameProcessed);
            succeeded = true;
        }
        finally
        {
            if (_trace.IsEnabled)
                _trace.RecordRenderStage(_traceSessionId, null, frame.Generation, frame.Sequence, "publish",
                    !succeeded ? "exception" : decision == GapRenderFrameDecision.None ? "published" : "capture-only",
                    started, Stopwatch.GetTimestamp(), frame.Width, frame.Height);
        }
    }

    private byte[] BorrowSnapshotPixels(RenderedFrameSnapshot frame)
    {
        // The caller holds the snapshot lease across this synchronous publication.
        // Mailbox replacement and native rendering cannot modify or return its array.
        if (!_trace.IsEnabled) return frame.Pixels;
        long started = Stopwatch.GetTimestamp(); string outcome = "exception";
        try { byte[] pixels = frame.Pixels; outcome = "borrowed"; return pixels; }
        finally { _trace.RecordRenderStage(_traceSessionId, null, frame.Generation, frame.Sequence, "ui-source", outcome, started, Stopwatch.GetTimestamp(), frame.Width, frame.Height); }
    }

    private void CopyFreezeWithTrace(RenderedFrameSnapshot frame, GapState state)
    {
        if (!_trace.IsEnabled) { _freezeCopier.CopyIfNeeded(frame.Pixels, state, frame.Width, frame.Height); return; }
        long started = Stopwatch.GetTimestamp(); string outcome = "exception";
        try { outcome = _freezeCopier.CopyIfNeeded(frame.Pixels, state, frame.Width, frame.Height) ? "copied" : "not-needed"; }
        finally { _trace.RecordRenderStage(_traceSessionId, null, frame.Generation, frame.Sequence, "freeze-copy", outcome, started, Stopwatch.GetTimestamp(), frame.Width, frame.Height); }
    }

    /// <summary>Redraw only after the owner verifies paused seek completion and media identity.</summary>
    public async Task<bool> TryCaptureGapFreezeFrameAsync(int generation, Func<bool> isAttemptCurrent)
    {
        ArgumentNullException.ThrowIfNull(isAttemptCurrent);
        bool copied = false;
        await _gate.RunAsync(async () =>
        {
            if (!IsCurrent(generation) || !isAttemptCurrent() || _context == IntPtr.Zero) return;
            if (SuppressFrameSnapshots)
            {
                // 合成層がフリーズ画像をソースから直接保存するため、CPU コピーは不要。
                copied = true;
                return;
            }
            using var frame = await _thread.InvokeAsync(() => RenderNativeSnapshot(generation));
            if (frame == null) return;
            if (!IsCurrent(generation) || !isAttemptCurrent()) { TraceFrame(frame, "discard", "stale-gap-capture"); return; }
            byte[] pixels = BorrowSnapshotPixels(frame);
            long started = _trace.IsEnabled ? Stopwatch.GetTimestamp() : 0;
            bool captureReturned = false;
            try
            {
                _buffers.EnsureFrozenFrameBuffer(frame.Width, frame.Height);
                copied = _buffers.TryCopyToFrozenFrame(pixels, frame.Width, frame.Height) &&
                    _buffers.TryCopyFrozenToGapFreezeFrame(frame.Width, frame.Height);
                captureReturned = true;
            }
            finally
            {
                if (_trace.IsEnabled)
                    _trace.RecordRenderStage(_traceSessionId, null, frame.Generation, frame.Sequence, "freeze-copy",
                        !captureReturned ? "exception" : copied ? "explicit-captured" : "rejected", started, Stopwatch.GetTimestamp(), frame.Width, frame.Height);
            }
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
            using var output = expectedDecision switch
            {
                GapRenderFrameDecision.Black => _outputFrames.Black(Width, Height),
                GapRenderFrameDecision.GapFreeze => _outputFrames.GapFreeze(Width, Height),
                _ => null
            };
            if (output != null) _publishPipeline.Publish(output, _spoutOutput.IsEnabled);
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
        try
        {
            lock (_submissionSync)
            {
                if (_stopped) return;
                _stopped = true;
                barrier = _thread.InvokeAsync(() => { });
            }
        }
        finally
        {
            // Stop is irreversible. Disposing rejects a QueueFrame that raced
            // the stopped check, even off-UI; UI resource cleanup never waits.
            // Also run after an already-stopped return or failed barrier submission.
            _preview?.Dispose();
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
        // The preview has no native-owned dependencies and can be released even
        // when context cleanup must retain mpv buffers and be retried.
        try { _preview?.Dispose(); }
        catch (Exception ex) { errors.Add(ex); }
        // On failure retain the thread, callback and pinned buffers alongside the live context.
        // FreeContext can be explicitly retried by a caller that knows the native failure is recoverable.
        try { FreeContext(); }
        catch (Exception ex)
        {
            errors.Add(ex);
            throw new AggregateException("RenderSession context cleanup failed", errors);
        }
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
