using System.Runtime.InteropServices;
using Serilog;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

/// <summary>
/// shim のフレーム通知を単一の専用スレッドで直列に drain し、世代付きで UI へ渡す。
/// 出荷構成（GStreamer + GPU 合成）では画像は合成層がソースから直接取るため、
/// ここは通知の駆動と寿命管理だけを行い、CPU へのフレームコピーはしない。
/// </summary>
internal sealed class RenderSession : IDisposable
{
    private readonly IMpvRenderApi _api;
    private readonly PlaybackPerformanceStats _stats;
    private readonly Action<Action> _scheduleUpdate;
    private readonly RenderThreadExecutor _thread = new();
    private readonly RenderUpdateGeneration _generation = new();
    private readonly IRenderUpdateScheduler _scheduler = new RenderUpdateScheduler();
    private readonly RenderUpdateScheduler _nativeScheduler = new();
    private readonly object _submissionSync = new();
    private IntPtr _context;
    // Native code does not root delegates. Retain this until RenderContextFree has returned.
    private RenderUpdateFn? _updateCallback;
    private int _pendingHasFrame;
    private volatile bool _stopped;
    private bool _disposed;

    public RenderSession(IMpvRenderApi api, PlaybackPerformanceStats stats, Action<Action> scheduleUpdate)
    {
        _api = api;
        _stats = stats;
        _scheduleUpdate = scheduleUpdate;
    }

    public Func<int, bool, Task>? FrameUpdate { get; set; }

    public int CaptureGeneration() => _generation.Capture();
    public bool IsCurrent(int generation) => !_stopped && _generation.IsCurrent(generation);
    public void Invalidate() => _generation.Advance();
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
            // ネイティブコールバック内では通常の API 呼び出し・UI 待ち・描画をしない。
            lock (_submissionSync)
            {
                if (_stopped || !_nativeScheduler.RequestDispatch()) return;
                _ = DrainNativeUpdatesAsync();
            }
        };
        _thread.InvokeAsync(() => _api.RenderContextSetUpdateCallback(_context, _updateCallback, IntPtr.Zero))
            .GetAwaiter().GetResult();
        Log.Information("mpv SW レンダーコンテキスト作成完了");
        return true;
    }

    private async Task DrainNativeUpdatesAsync()
    {
        try
        {
            await _thread.InvokeAsync(() =>
            {
                try
                {
                    if (_stopped) return;
                    var update = ReadNativeUpdate();
                    if (IsCurrent(update.Generation))
                    {
                        if (update.HasFrame) Interlocked.Exchange(ref _pendingHasFrame, 1);
                        if (_scheduler.RequestDispatch()) _scheduleUpdate(OnRenderUpdate);
                    }
                }
                finally
                {
                    // A playing frame can signal its successor before render returns.
                    // Yield the executor queue after each frame so explicit updates
                    // and shutdown cannot be starved by a perpetual drain loop.
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

    private (int Generation, bool HasFrame) ReadNativeUpdate()
    {
        int generation = CaptureGeneration();
        ulong flags = _api.RenderContextUpdate(_context);
        return (generation, (flags & _api.MpvRenderUpdateFrame) != 0);
    }

    private async void OnRenderUpdate()
    {
        try
        {
            await AsyncOperationExceptionBoundary.RunAsync(
                () => ProcessUpdateCoreAsync(TakePendingUpdate(),
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
        var update = await _thread.InvokeAsync(ReadNativeUpdate);
        await ProcessUpdateCoreAsync(update, processFrame);
    }

    private (int Generation, bool HasFrame) TakePendingUpdate() =>
        (CaptureGeneration(), Interlocked.Exchange(ref _pendingHasFrame, 0) != 0);

    private async Task ProcessUpdateCoreAsync((int Generation, bool HasFrame) update,
        Func<int, bool, Task> processFrame)
    {
        if (!IsCurrent(update.Generation)) return;
        _stats.RecordRenderUpdate(update.HasFrame);
        await processFrame(update.Generation, update.HasFrame);
    }

    /// <summary>
    /// Gap フリーズの最終フレームは GPU 合成層が進入時に保存する（ComposeLayer.SaveFreeze）。
    /// ここは状態機械の「キャプチャ完了」を、世代が変わっていないことだけで確認する。
    /// </summary>
    public Task<bool> TryCaptureGapFreezeFrameAsync(int generation, Func<bool> isAttemptCurrent)
    {
        ArgumentNullException.ThrowIfNull(isAttemptCurrent);
        return Task.FromResult(IsCurrent(generation) && isAttemptCurrent() && _context != IntPtr.Zero);
    }

    /// <summary>Disable callbacks and wait only for native work, never UI continuations.</summary>
    public void Stop()
    {
        Task barrier;
        lock (_submissionSync)
        {
            if (_stopped) return;
            _stopped = true;
            barrier = _thread.InvokeAsync(() => { });
        }
        // Every queued native operation precedes this barrier.
        barrier.GetAwaiter().GetResult();
    }

    /// <summary>Must succeed before the shim player is destroyed. Called on the owning UI thread.</summary>
    public void FreeContext()
    {
        Stop();
        if (_context == IntPtr.Zero) return;
        _thread.InvokeAsync(() => _api.RenderContextFree(_context)).GetAwaiter().GetResult();
        _context = IntPtr.Zero;
        _updateCallback = null;
    }

    /// <summary>After FreeContext succeeds, join the idle native thread.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        var errors = new List<Exception>();
        // On failure retain the thread, callback and pinned buffers alongside the live context.
        // FreeContext can be explicitly retried by a caller that knows the native failure is recoverable.
        try { FreeContext(); }
        catch (Exception ex)
        {
            errors.Add(ex);
            throw new AggregateException("RenderSession context cleanup failed", errors);
        }
        _disposed = true;
        try { _thread.Dispose(); }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException("RenderSession resource cleanup failed", errors);
    }
}
