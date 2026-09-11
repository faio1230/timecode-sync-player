using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Serilog;
using Vortice.Direct3D11;

namespace TimecodeSyncPlayer.Output;

/// <summary>
/// Spout 送信 worker 側の送信機。別デバイスの共有テクスチャを保持画像へ GPU コピーし、外側アクセス mutex を
/// 要求 8ms（期限＝次回予定）で取得して SendTexture する。取得失敗は保持画像の再送で、資源は無効化しない。
/// 試作 scripts/GpuOutputProbe の SpoutSender を移植（copy-retry は確認済み設計に含めない）。
/// </summary>
internal sealed class SpoutSender : IDisposable
{
    public const int AllocationBytes = 4096;
    public const int VerifiedSdkSize = 1864;
    public const string VerifiedSdkHash = "BBCEE6F0031F6BD1A6461585C53F3F8F3CECBF006B486661BCDE6413E2ADDEF5";
    public const string SenderNameEnvironmentVariable = "TIMECODE_SYNC_PLAYER_SPOUT_NAME";

    private readonly GpuDevice gpu;
    private readonly OutputTrace log;
    private readonly string worker;
    private readonly int mutexWaitMs;
    private readonly SharedFenceReader? fence;
    private readonly ID3D11Texture2D heldTexture;
    private IntPtr self;
    private Mutex? mutex;
    private ImageStamp held;
    public string ActualName { get; }

    public SpoutSender(GpuDevice gpu, OutputTrace log, string worker, string senderName, int width, int height, int mutexWaitMs = MutexWaitPolicy.MaxWaitMs, SharedFenceReader? fence = null)
    {
        SpoutTextureNative.EnsureVerifiedLibrary(warnOnly: true);
        this.gpu = gpu; this.log = log; this.worker = worker; this.mutexWaitMs = mutexWaitMs; this.fence = fence;
        heldTexture = gpu.Texture(width, height, SourceSharing.None);
        bool constructed = false;
        try
        {
            self = Marshal.AllocHGlobal(AllocationBytes);
            unsafe { new Span<byte>((void*)self, AllocationBytes).Clear(); }
            SpoutTextureNative.Ctor(self); constructed = true;
            if (!SpoutTextureNative.OpenDirectX11(self, gpu.Device.NativePointer)) throw new InvalidOperationException("Spout OpenDirectX11 failed.");
            if (!SpoutTextureNative.SetSenderName(self, senderName)) throw new InvalidOperationException($"Spout SetSenderName failed: {senderName}");
            if (!SpoutTextureNative.CheckSender(self, (uint)width, (uint)height, 87)) throw new InvalidOperationException("Spout CheckSender failed.");
            ActualName = Marshal.PtrToStringAnsi(SpoutTextureNative.GetSenderName(self)) ?? throw new InvalidOperationException("No sender name.");
            mutex = Mutex.OpenExisting(ActualName + "_SpoutAccessMutex");
            log.Add("lifecycle", worker, detail: "sender.ready:" + ActualName);
        }
        catch
        {
            try { gpu.Fence.Wait("Spout.constructor.drain"); }
            catch (GpuDeviceLostException) { }
            finally
            {
                try { if (constructed) SpoutTextureNative.Dtor(self); }
                finally { if (self != IntPtr.Zero) Marshal.FreeHGlobal(self); self = IntPtr.Zero; mutex?.Dispose(); heldTexture.Dispose(); }
            }
            throw;
        }
    }

    private enum CopyOutcome { Copied, SameHeld, NoLease, Busy }

    public void Update(Surface[] sources, LatestPool pool, long scheduled, CancellationToken stop)
        => _ = CopyOnce(sources, pool, scheduled, "first", out _);

    private CopyOutcome CopyOnce(Surface[] sources, LatestPool pool, long scheduled, string kind, out ImageStamp attempted)
    {
        long selectStarted = Stopwatch.GetTimestamp();
        using var lease = pool.AcquireLatest();
        long selectEnded = Stopwatch.GetTimestamp();
        ImageStamp selected = lease?.Stamp ?? default;
        attempted = selected;
        string selection = lease == null ? "none" : selected.Id == held.Id ? "retained" : "latest";
        long attempt = kind == "first" ? 0 : 1;
        log.Record(new("send.select.start", worker, selectStarted, scheduled, selected.Id, selected.GeneratedQpc, Value: attempt));
        log.Record(new("send.select.end", worker, selectEnded, scheduled, selected.Id, selected.GeneratedQpc, selection, Value: attempt));
        switch (SpoutOutputPolicy.DecideCopy(lease?.Stamp.Id ?? 0, held.Id))
        {
            case SpoutCopyDecision.NoImage:
                return CopyOutcome.NoLease;
            case SpoutCopyDecision.SameHeld:
                return CopyOutcome.SameHeld;
        }
        var source = sources[lease!.Slot];
        if (fence != null)
        {
            // 合成完了を GPU キューで待つ（フェンス値＝画像 ID）。CPU では何も保持しない。
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
                // 最終期限確認とネイティブ呼び出しの間に記録処理を挟まない。
                submitted = true;
                sent = SpoutTextureNative.SendTexture(self, heldTexture.NativePointer);
                sendReturned = Stopwatch.GetTimestamp();
            }
            finally { log.Record(new("send.start", worker, sendStarted, scheduled, held.Id, held.GeneratedQpc)); }
            log.Record(new("send.return", worker, sendReturned, scheduled, held.Id, held.GeneratedQpc, sent ? "true" : "false"));
            gpu.Fence.Wait("sender.send");
            completed = true;
            log.Add("send.gpuComplete", worker, scheduled, held);
            if (!sent) throw new InvalidOperationException("Spout SendTexture returned false.");
            acquisition.Dispose();
            log.Add("send.publish", worker, scheduled, held);
        }
        finally
        {
            if (submitted && !completed) gpu.Fence.Wait("sender.send.drain");
        }
    }

    public void Dispose()
    {
        if (self != IntPtr.Zero)
        {
            SpoutTextureNative.ReleaseSender(self); SpoutTextureNative.Dtor(self); Marshal.FreeHGlobal(self); self = IntPtr.Zero;
        }
        mutex?.Dispose(); mutex = null; heldTexture.Dispose();
    }
}

internal static class SpoutTextureNative
{
    private const string Dll = "SpoutDX.dll";
    private static readonly object libraryGate = new();
    private static bool verified;

    internal static void EnsureVerifiedLibrary(bool warnOnly)
    {
        lock (libraryGate)
        {
            if (verified) return;
            verified = true;
            try
            {
                string path = Path.Combine(AppContext.BaseDirectory, Dll);
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                if (hash != SpoutSender.VerifiedSdkHash)
                    Log.Warning("SpoutDX.dll のハッシュが ABI 検証済みと異なります: {Hash}", hash);
            }
            catch (Exception ex)
            {
                if (!warnOnly) throw;
                Log.Warning(ex, "SpoutDX.dll の確認に失敗しました");
            }
        }
    }

    [DllImport(Dll, EntryPoint = "??0spoutDX@@QEAA@XZ", CallingConvention = CallingConvention.ThisCall)]
    internal static extern void Ctor(IntPtr self);

    [DllImport(Dll, EntryPoint = "??1spoutDX@@QEAA@XZ", CallingConvention = CallingConvention.ThisCall)]
    internal static extern void Dtor(IntPtr self);

    [DllImport(Dll, EntryPoint = "?OpenDirectX11@spoutDX@@QEAA_NPEAUID3D11Device@@@Z", CallingConvention = CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool OpenDirectX11(IntPtr self, IntPtr device);

    [DllImport(Dll, EntryPoint = "?SetSenderName@spoutDX@@QEAA_NPEBD@Z", CallingConvention = CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SetSenderName(IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Dll, EntryPoint = "?SendTexture@spoutDX@@QEAA_NPEAUID3D11Texture2D@@@Z", CallingConvention = CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SendTexture(IntPtr self, IntPtr texture);

    [DllImport(Dll, EntryPoint = "?CheckSender@spoutDX@@IEAA_NIIK@Z", CallingConvention = CallingConvention.ThisCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool CheckSender(IntPtr self, uint width, uint height, uint format);

    [DllImport(Dll, EntryPoint = "?GetSenderName@spoutDX@@QEAAPEBDXZ", CallingConvention = CallingConvention.ThisCall)]
    internal static extern IntPtr GetSenderName(IntPtr self);

    [DllImport(Dll, EntryPoint = "?ReleaseSender@spoutDX@@QEAAXXZ", CallingConvention = CallingConvention.ThisCall)]
    internal static extern void ReleaseSender(IntPtr self);
}
