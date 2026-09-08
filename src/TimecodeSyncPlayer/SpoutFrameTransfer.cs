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
    private ISpoutTransferMutex? _mutex;
    private string? _name;
    private bool _disposed;

    internal SpoutFrameTransfer(ISpoutTransferBackend backend, ISpoutGpuCompletion completion,
        Func<string, ISpoutTransferMutex> createMutex, Func<long> milliseconds)
    {
        _backend = backend; _completion = completion; _createMutex = createMutex; _milliseconds = milliseconds;
    }

    public bool SendImage(IntPtr pixels, uint width, uint height, uint pitch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string name = _backend.Prepare(width, height);
        if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Spout sender has no registered name.");
        if (_name != name)
        {
            _mutex?.Dispose(); _mutex = null; _name = null;
            _mutex = _createMutex(name); _name = name;
        }
        bool acquired = false;
        try
        {
            try { acquired = _mutex!.Wait(100); }
            catch (AbandonedMutexException) { acquired = true; throw; }
            if (!acquired) throw new TimeoutException($"Spout mutex wait timed out: {name}");
            if (!_backend.SendImage(pixels, width, height, pitch)) return false;
            long start = _milliseconds();
            _completion.End();
            _completion.Flush();
            // Match the native fence loop: yield without escalating to Sleep(1).
            // SpinWait caused EVENT polling timeouts on the validated driver; Thread.Yield
            // completed 1800 4K sends in 30s. Keep the independent 100ms deadline below.
            while (true)
            {
                int hr = _completion.GetData(out int completed);
                if (hr < 0) throw new System.Runtime.InteropServices.COMException($"Spout GPU completion failed (HRESULT 0x{hr:X8}).", hr);
                if (_milliseconds() - start >= 100) throw new TimeoutException("Spout GPU completion exceeded 100 ms.");
                if (hr == 0 && completed != 0) return true;
                Thread.Yield();
            }
        }
        finally { if (acquired) _mutex!.Release(); }
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
