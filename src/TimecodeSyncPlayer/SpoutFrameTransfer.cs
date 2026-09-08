using System.Diagnostics;
using Serilog;

namespace TimecodeSyncPlayer;

internal interface ISpoutFrameTransfer : IDisposable
{
    bool SendImage(IntPtr pixels, uint width, uint height, uint pitch);
}

internal interface ISpoutTransferBackend
{
    string Prepare(uint width, uint height);
    bool SendImage(IntPtr pixels, uint width, uint height, uint pitch);
}

internal interface ISpoutTransferMutex : IDisposable
{
    bool Wait(int milliseconds);
    void Release();
}

internal interface ISpoutGpuCompletion : IDisposable
{
    void End();
    void Flush();
    int GetData(out int completed);
}

// UI-thread owned; no asynchronous work is permitted while the OS mutex is held.
internal sealed class SpoutFrameTransfer : ISpoutFrameTransfer
{
    private readonly ISpoutTransferBackend _backend;
    private readonly ISpoutGpuCompletion _completion;
    private readonly Func<string, ISpoutTransferMutex> _createMutex;
    private readonly Func<long> _milliseconds;
    private readonly Func<long>? _timestamp;
    private readonly ILogger? _logger;
    private ISpoutTransferMutex? _mutex;
    private string? _name;
    private bool _disposed;

    internal SpoutFrameTransfer(ISpoutTransferBackend backend, ISpoutGpuCompletion completion,
        Func<string, ISpoutTransferMutex> createMutex, Func<long> milliseconds,
        Func<long>? timestamp = null, ILogger? logger = null)
    {
        _backend = backend; _completion = completion; _createMutex = createMutex; _milliseconds = milliseconds;
        _timestamp = timestamp; _logger = logger;
    }

    public bool SendImage(IntPtr pixels, uint width, uint height, uint pitch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long entered = Timestamp();
        var timing = new TransferTiming();
        timing.Begin("Prepare", entered);
        string? failureStage = null;
        Exception? transferFailure = null;
        string? attemptedName = null;
        int polls = 0;
        int? lastHr = null, lastCompleted = null;
        long? gpuElapsedMs = null;
        bool sendSucceeded = false;
        bool sendReturnedFalse = false;
        try
        {
            string name = _backend.Prepare(width, height);
            attemptedName = name;
            if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Spout sender has no registered name.");
            timing.Begin("CreateMutex", Timestamp());
            if (_name != name)
            {
                _mutex?.Dispose(); _mutex = null; _name = null;
                _mutex = _createMutex(name); _name = name;
            }
            bool acquired = false;
            try
            {
                timing.Begin("WaitMutex", Timestamp());
                try { acquired = _mutex!.Wait(100); }
                catch (AbandonedMutexException) { acquired = true; throw; }
                if (!acquired) throw new TimeoutException($"Spout mutex wait timed out: {name}");
                timing.Begin("SendImage", Timestamp());
                if (!_backend.SendImage(pixels, width, height, pitch)) { sendReturnedFalse = true; return false; }
                timing.Begin("End", Timestamp());
                long start = _milliseconds();
                _completion.End();
                timing.Begin("Flush", Timestamp());
                _completion.Flush();
                timing.Begin("GetData", Timestamp());
                // Match the native fence loop: yield without escalating to Sleep(1).
                // SpinWait caused EVENT polling timeouts on the validated driver; Thread.Yield
                // completed 1800 4K sends in 30s. Keep the independent 100ms deadline below.
                while (true)
                {
                    polls++;
                    int hr = _completion.GetData(out int completed);
                    lastHr = hr; lastCompleted = completed;
                    if (hr < 0)
                    {
                        gpuElapsedMs = _milliseconds() - start;
                        throw new System.Runtime.InteropServices.COMException($"Spout GPU completion failed (HRESULT 0x{hr:X8}).", hr);
                    }
                    gpuElapsedMs = _milliseconds() - start;
                    // The calling thread may resume after the deadline with a
                    // completed query. Completion proves the shared image is ready;
                    // only a still-pending query requires the timeout failure path.
                    if (hr == 0 && completed != 0) { sendSucceeded = true; return true; }
                    if (gpuElapsedMs >= 100) throw new TimeoutException("Spout GPU completion exceeded 100 ms.");
                    Thread.Yield();
                }
            }
            catch (Exception ex)
            {
                failureStage = timing.Stage;
                transferFailure = ex;
                throw;
            }
            finally
            {
                if (acquired)
                {
                    timing.Begin("ReleaseMutex", Timestamp());
                    try { _mutex!.Release(); }
                    catch (Exception ex)
                    {
                        ex.Data["SpoutTransferPriorStage"] = failureStage;
                        if (transferFailure != null) ex.Data["SpoutTransferPriorException"] = transferFailure;
                        failureStage = "ReleaseMutex";
                        throw;
                    }
                    finally { timing.End(Timestamp()); }
                }
            }
        }
        catch (Exception ex)
        {
            // Only format on failure, after leaving the mutex. Preserve the final
            // query result for pending timeouts and native failures.
            // These times exclude SpoutOutput's invalidation/native destruction.
            timing.End(Timestamp());
            ex.Data["SpoutTransfer"] = $"stage={failureStage ?? timing.Stage}; sender={attemptedName ?? "<unregistered>"}; " +
                $"size={width}x{height}; polls={polls}; hr={lastHr?.ToString("X8") ?? "n/a"}; " +
                $"completed={lastCompleted}; gpuElapsedMs={gpuElapsedMs}; " +
                $"prepareMs={Duration(timing.PrepareMs)}; createMutexMs={Duration(timing.CreateMutexMs)}; mutexMs={Duration(timing.MutexMs)}; " +
                $"sendMs={Duration(timing.SendMs)}; endMs={Duration(timing.EndMs)}; flushMs={Duration(timing.FlushMs)}; " +
                $"pollMs={Duration(timing.PollMs)}; releaseMutexMs={Duration(timing.ReleaseMutexMs)}; " +
                $"totalBeforeCleanupMs={Elapsed(entered, Timestamp()):F3}" +
                (ex.Data["SpoutTransferPriorException"] is Exception prior
                    ? $"; priorFailureStage={ex.Data["SpoutTransferPriorStage"]}; priorFailure={prior}" : "");
            throw;
        }
        finally
        {
            // Keep successful frames allocation-free here; create log values only
            // over the 60 Hz budget, after the mutex has been released. In-memory
            // probe sinks can retain these values without rendering or disk I/O.
            long finished = Timestamp();
            if (failureStage == null && (sendReturnedFalse || (sendSucceeded && Elapsed(entered, finished) > 1000.0 / 60)))
            {
                (_logger ?? Log.Logger).Warning(sendReturnedFalse
                    ? "SpoutFrameTransfer: SendImage returned false {@Transfer}"
                    : "SpoutFrameTransfer: slow send {@Transfer}", new
                {
                    Sender = attemptedName, Width = width, Height = height,
                    Stage = sendReturnedFalse ? "SendImage" : "Completed", SendSucceeded = sendSucceeded,
                    StartQpc = entered, EndQpc = finished, QpcFrequency = Stopwatch.Frequency,
                    Polls = polls, LastHResult = lastHr, LastCompleted = lastCompleted,
                    GpuElapsedMs = gpuElapsedMs, timing.PrepareMs, timing.CreateMutexMs,
                    timing.MutexMs, timing.SendMs, timing.EndMs, timing.FlushMs,
                    timing.PollMs, timing.ReleaseMutexMs, TotalBeforeCleanupMs = Elapsed(entered, finished)
                });
            }
        }
    }

    private long Timestamp() => _timestamp?.Invoke() ?? Stopwatch.GetTimestamp();
    private static double Elapsed(long start, long end) => Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
    private static string Duration(double? milliseconds) => milliseconds?.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) ?? "not-started";

    private struct TransferTiming
    {
        internal string? Stage;
        private long _started;
        private bool _running;
        internal double? PrepareMs, CreateMutexMs, MutexMs, SendMs, EndMs, FlushMs, PollMs, ReleaseMutexMs;

        internal void Begin(string stage, long timestamp)
        {
            End(timestamp);
            Stage = stage; _started = timestamp; _running = true;
        }

        internal void End(long timestamp)
        {
            if (!_running) return;
            double elapsed = Elapsed(_started, timestamp);
            switch (Stage)
            {
                case "Prepare": PrepareMs = elapsed; break;
                case "CreateMutex": CreateMutexMs = elapsed; break;
                case "WaitMutex": MutexMs = elapsed; break;
                case "SendImage": SendMs = elapsed; break;
                case "End": EndMs = elapsed; break;
                case "Flush": FlushMs = elapsed; break;
                case "GetData": PollMs = elapsed; break;
                case "ReleaseMutex": ReleaseMutexMs = elapsed; break;
            }
            _running = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _completion.Dispose(); }
        finally { _mutex?.Dispose(); _mutex = null; }
    }
}

internal sealed class SpoutTransferMutex(string name) : ISpoutTransferMutex
{
    private readonly Mutex _mutex = new(false, name + "_SpoutAccessMutex");
    public bool Wait(int milliseconds) => _mutex.WaitOne(milliseconds);
    public void Release() => _mutex.ReleaseMutex();
    public void Dispose() => _mutex.Dispose();
}

internal sealed class SpoutTransferBackend(IntPtr self) : ISpoutTransferBackend
{
    public string Prepare(uint width, uint height)
    {
        // Current SpoutDX defaults to DXGI_FORMAT_B8G8R8A8_UNORM (87); no SetSenderFormat is used.
        if (!SpoutNative.CheckSender(self, width, height, 87))
            throw new InvalidOperationException("Spout CheckSender failed.");
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(SpoutNative.GetSenderName(self))
            ?? throw new InvalidOperationException("Spout GetSenderName returned null.");
    }
    public bool SendImage(IntPtr pixels, uint width, uint height, uint pitch) => SpoutNative.SendImage(self, pixels, width, height, pitch);
}
