using System.Runtime.InteropServices;
using System.IO;
using System.Security.Cryptography;
using System.Diagnostics;
using Vortice.Direct3D11;

namespace GpuOutputProbe;

internal sealed class SpoutSender : IDisposable
{
    private readonly GpuDevice gpu;
    private readonly ProbeLog log;
    private readonly string worker;
    private readonly int mutexWaitMs;
    private readonly ManualResetEventSlim? copyReleased;
    private readonly bool retryEnabled;
    private readonly SharedFenceReader? fence;
    private readonly ID3D11Texture2D heldTexture;
    private IntPtr self;
    private Mutex? mutex;
    private ImageStamp held;
    public string ActualName { get; }
    // Verified against the matching SDK headers with x64 /MT /D_ITERATOR_DEBUG_LEVEL=0: sizeof=1864, alignof=8.
    // Keep the existing application's 4096-byte allocation. This is not a general ABI-size detector.
    public const int AllocationBytes = 4096;
    public const int VerifiedSdkSize = 1864;
    public SpoutSender(GpuDevice gpu, Options options, ProbeLog log, string worker, ManualResetEventSlim? copyReleased = null, SharedFenceReader? fence = null)
    {
        NativeSpout.EnsureVerifiedLibrary();
        this.gpu = gpu; this.log = log; this.worker = worker; mutexWaitMs = options.MutexWaitMs;
        this.copyReleased = copyReleased; this.fence = fence;
        retryEnabled = options.CopyRetry == "signal" && copyReleased != null && fence == null;
        heldTexture = gpu.Texture(options.Width, options.Height, SourceSharing.None);
        bool constructed = false;
        try
        {
            self = Marshal.AllocHGlobal(AllocationBytes);
            unsafe { new Span<byte>((void*)self, AllocationBytes).Clear(); }
            NativeSpout.Ctor(self); constructed = true;
            if (!NativeSpout.OpenDirectX11(self, gpu.Device.NativePointer)) throw new InvalidOperationException("Spout OpenDirectX11 failed.");
            if (!NativeSpout.SetSenderName(self, options.Sender)) throw new InvalidOperationException("Spout SetSenderName failed.");
            if (!NativeSpout.CheckSender(self, (uint)options.Width, (uint)options.Height, 87)) throw new InvalidOperationException("Spout CheckSender failed.");
            ActualName = Marshal.PtrToStringAnsi(NativeSpout.GetSenderName(self)) ?? throw new InvalidOperationException("No sender name.");
            mutex = Mutex.OpenExisting(ActualName + "_SpoutAccessMutex");
            log.Add("lifecycle", worker, detail: "sender.ready:" + ActualName);
        }
        catch
        {
            try { gpu.Fence.Wait("Spout.constructor.drain"); }
            catch (GpuDeviceLostException) { /* Invalid GPU resources may be destroyed after confirmed removal. */ }
            finally
            {
                try { if (constructed) NativeSpout.Dtor(self); }
                finally { if (self != IntPtr.Zero) Marshal.FreeHGlobal(self); self = IntPtr.Zero; mutex?.Dispose(); heldTexture.Dispose(); }
            }
            throw;
        }
    }
    private enum CopyOutcome { Copied, SameHeld, NoLease, Busy }
    public void Update(Surface[] sources, LatestPool pool, long scheduled, long nextScheduled, CancellationToken stop)
    {
        var first = CopyOnce(sources, pool, scheduled, "first", out ImageStamp attempted);
        if (first != CopyOutcome.Busy || !retryEnabled) return;
        // Only the busy path arrives here; no lease, pool entry, or keyed mutex is held during the wait.
        long now = Stopwatch.GetTimestamp();
        int budget = CopyRetryGate.BudgetMs(now, nextScheduled, Stopwatch.Frequency);
        if (budget <= 0 || stop.IsCancellationRequested) { log.Add("skip", worker, scheduled, attempted, budget <= 0 ? "copy.retry.deadline" : "copy.retry.cancelled", 1); return; }
        log.Record(new("copy.retry", worker, now, scheduled, attempted.Id, attempted.GeneratedQpc, Value: budget, DeadlineQpc: nextScheduled));
        // Clear the stale signal first; only a release after this failed acquire may wake the wait.
        // A missed release between acquire and Reset costs at most the budget, after which the single retry runs anyway.
        copyReleased!.Reset();
        // A signal or the whole budget exits the wait identically; the outcome does not branch the retry itself.
        _ = copyReleased.Wait(budget);
        if (stop.IsCancellationRequested) { log.Add("skip", worker, scheduled, attempted, "copy.retry.cancelled", 1); return; }
        if (Stopwatch.GetTimestamp() >= nextScheduled) { log.Add("skip", worker, scheduled, attempted, "copy.retry.deadline", 1); return; }
        // A second busy failure records copy.keyedMutexBusy.retry; never retry again within this output slot.
        _ = CopyOnce(sources, pool, scheduled, "retry", out _);
    }
    private CopyOutcome CopyOnce(Surface[] sources, LatestPool pool, long scheduled, string kind, out ImageStamp attempted)
    {
        long selectStarted = Stopwatch.GetTimestamp();
        using var lease = pool.AcquireLatest();
        long selectEnded = Stopwatch.GetTimestamp();
        ImageStamp selected = lease?.Stamp ?? default;
        attempted = selected;
        string selection = lease == null ? "none" : selected.Id == held.Id ? "retained" : "latest";
        // Attempt index: 0 keeps old logs identical; 1 lets the analyzer pair the same-slot retry separately.
        long attempt = kind == "first" ? 0 : 1;
        log.Record(new("send.select.start", worker, selectStarted, scheduled, selected.Id, selected.GeneratedQpc, Value: attempt));
        log.Record(new("send.select.end", worker, selectEnded, scheduled, selected.Id, selected.GeneratedQpc, selection, Value: attempt));
        if (lease == null) return CopyOutcome.NoLease;
        if (lease.Stamp.Id == held.Id) return CopyOutcome.SameHeld;
        var source = sources[lease.Slot];
        if (fence != null)
        {
            // GPU-queue wait for compose completion of this image (fence value == image id); nothing is held on the CPU.
            fence.Wait(lease.Stamp.Id);
            log.Record(new("copy.fence.wait", worker, Stopwatch.GetTimestamp(), scheduled, lease.Stamp.Id, lease.Stamp.GeneratedQpc, Detail: kind + ":" + lease.Slot, Value: lease.Stamp.Id));
        }
        else
        {
            if (!source.Acquire()) { log.Add("skip", worker, scheduled, lease.Stamp, kind == "first" ? "copy.keyedMutexBusy" : "copy.keyedMutexBusy.retry", 1); return CopyOutcome.Busy; }
            log.Record(new("copy.mutex.acquire", worker, Stopwatch.GetTimestamp(), scheduled, lease.Stamp.Id, lease.Stamp.GeneratedQpc, Detail: kind + ":" + lease.Slot));
        }
        bool started = false;
        try
        {
            lease.BeginGpuUse(); started = true;
            log.Add("copy.start", worker, scheduled, lease.Stamp);
            gpu.Context.CopyResource(heldTexture, source.Texture);
            gpu.Fence.Wait("sender.copy");
            lease.CompleteGpuUse(); started = false;
            held = lease.Stamp;
            log.Add("copy.complete", worker, scheduled, held);
        }
        finally
        {
            // If an operation threw after submission, drain before returning the source lease/mutex.
            try { if (started) gpu.Fence.Wait("sender.copy.drain"); }
            finally
            {
                if (started) lease.CompleteGpuUse();
                if (fence == null)
                {
                    source.Release();
                    log.Record(new("copy.mutex.release", worker, Stopwatch.GetTimestamp(), scheduled, lease.Stamp.Id, lease.Stamp.GeneratedQpc, Detail: kind + ":" + lease.Slot));
                }
            }
        }
        return CopyOutcome.Copied;
    }
    public void Send(long scheduled, long nextScheduledQpc, CancellationToken cancellation)
    {
        if (held.Id == 0) { log.Add("skip", worker, scheduled, detail: "send.noImage", value: 1); return; }
        using var acquisition = SendMutexGate.Acquire(mutexWaitMs, nextScheduledQpc, Stopwatch.Frequency, cancellation,
            Stopwatch.GetTimestamp, timeout => mutex!.WaitOne(timeout), () => mutex!.ReleaseMutex(),
            attempt =>
            {
                log.Record(new("send.acquire.start", worker, attempt.StartQpc, scheduled, held.Id, held.GeneratedQpc, Value: attempt.TimeoutMs, DeadlineQpc: attempt.DeadlineQpc));
                log.Record(new("send.acquire.end", worker, attempt.EndQpc, scheduled, held.Id, held.GeneratedQpc, attempt.Outcome, DeadlineQpc: attempt.DeadlineQpc));
            }, reason => log.Add("skip", worker, scheduled, held, reason, 1));
        if (acquisition == null) return;
        string? skipReason = MutexWaitPolicy.SkipReason(Stopwatch.GetTimestamp(), nextScheduledQpc, cancellation.IsCancellationRequested);
        if (skipReason != null) { log.Add("skip", worker, scheduled, held, skipReason, 1); return; }
        bool submitted = false, completed = false;
        try
        {
            bool sent;
            long sendStarted = Stopwatch.GetTimestamp(), sendReturned;
            skipReason = MutexWaitPolicy.SkipReason(sendStarted, nextScheduledQpc, cancellation.IsCancellationRequested);
            if (skipReason != null) { log.Add("skip", worker, scheduled, held, skipReason, 1); return; }
            try
            {
                // No diagnostic enqueue between the final deadline guard and the native call.
                submitted = true;
                sent = NativeSpout.SendTexture(self, heldTexture.NativePointer);
                sendReturned = Stopwatch.GetTimestamp();
            }
            finally { log.Record(new("send.start", worker, sendStarted, scheduled, held.Id, held.GeneratedQpc)); }
            log.Record(new("send.return", worker, sendReturned, scheduled, held.Id, held.GeneratedQpc, sent ? "true" : "false"));
            // Fence held inside outer recursive OS mutex, including a false-return path that may have submitted work.
            gpu.Fence.Wait("sender.send");
            completed = true;
            log.Add("send.gpuComplete", worker, scheduled, held);
            if (!sent) throw new InvalidOperationException("Spout SendTexture returned false.");
            acquisition.Dispose();
            log.Add("send.publish", worker, scheduled, held);
        }
        finally
        {
            // The using lease releases the OS mutex after this drain. No GPU fence is submitted for a skipped acquisition.
            if (submitted && !completed) gpu.Fence.Wait("sender.send.drain");
        }
    }
    public void Dispose()
    {
        if (self != IntPtr.Zero)
        {
            NativeSpout.ReleaseSender(self); NativeSpout.Dtor(self); Marshal.FreeHGlobal(self); self = IntPtr.Zero;
        }
        mutex?.Dispose(); mutex = null; heldTexture.Dispose();
    }
}
internal static class NativeSpout
{
    private const string Dll = "SpoutDX.dll";
    private static readonly object libraryGate = new();
    private static IntPtr library;
    internal static void EnsureVerifiedLibrary()
    {
        lock (libraryGate)
        {
            if (library != IntPtr.Zero) return;
            string path = Path.Combine(AppContext.BaseDirectory, Dll);
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            const string verifiedHash = "BBCEE6F0031F6BD1A6461585C53F3F8F3CECBF006B486661BCDE6413E2ADDEF5";
            if (hash != verifiedHash) throw new InvalidOperationException("Spout DLL differs from the SDK used for ABI verification. Verify sizeof/exports before updating this probe.");
            // Keep module loaded until process exit; imports cannot outlive it. Load only the verified absolute file.
            library = NativeLibrary.Load(path);
            NativeLibrary.SetDllImportResolver(typeof(NativeSpout).Assembly, (name, _, _) => name == Dll ? library : IntPtr.Zero);
        }
    }
    [DllImport(Dll, EntryPoint="??0spoutDX@@QEAA@XZ", CallingConvention=CallingConvention.ThisCall)] internal static extern void Ctor(IntPtr self);
    [DllImport(Dll, EntryPoint="??1spoutDX@@QEAA@XZ", CallingConvention=CallingConvention.ThisCall)] internal static extern void Dtor(IntPtr self);
    [DllImport(Dll, EntryPoint="?OpenDirectX11@spoutDX@@QEAA_NPEAUID3D11Device@@@Z", CallingConvention=CallingConvention.ThisCall)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool OpenDirectX11(IntPtr self, IntPtr device);
    [DllImport(Dll, EntryPoint="?SetSenderName@spoutDX@@QEAA_NPEBD@Z", CallingConvention=CallingConvention.ThisCall)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool SetSenderName(IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Dll, EntryPoint="?SendTexture@spoutDX@@QEAA_NPEAUID3D11Texture2D@@@Z", CallingConvention=CallingConvention.ThisCall)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool SendTexture(IntPtr self, IntPtr texture);
    [DllImport(Dll, EntryPoint="?CheckSender@spoutDX@@IEAA_NIIK@Z", CallingConvention=CallingConvention.ThisCall)] [return:MarshalAs(UnmanagedType.I1)] internal static extern bool CheckSender(IntPtr self, uint width, uint height, uint format);
    [DllImport(Dll, EntryPoint="?GetSenderName@spoutDX@@QEAAPEBDXZ", CallingConvention=CallingConvention.ThisCall)] internal static extern IntPtr GetSenderName(IntPtr self);
    [DllImport(Dll, EntryPoint="?ReleaseSender@spoutDX@@QEAAXXZ", CallingConvention=CallingConvention.ThisCall)] internal static extern void ReleaseSender(IntPtr self);
}
internal static partial class Native
{
    // IDXGIKeyedMutex::AcquireSync is slot8 after IUnknown, IDXGIObject and IDXGIDeviceSubObject.
    // Vortice's generated void wrapper drops WAIT_TIMEOUT/WAIT_ABANDONED positive HRESULTs.
    internal static unsafe int ReleaseKeyedMutex(IntPtr self)
    {
        var method = (delegate* unmanaged[Stdcall]<IntPtr, ulong, int>)(*(IntPtr**)self)[9];
        return method(self, 0);
    }
    internal static unsafe int AcquireKeyedMutex(IntPtr self)
    {
        var method = (delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, int>)(*(IntPtr**)self)[8];
        return method(self, 0, 0);
    }
}

