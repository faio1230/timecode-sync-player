using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Serilog;
using Vortice.Direct3D11;

namespace TimecodeSyncPlayer.Output;

/// <summary>
/// Spout 送信機（段階 3 で同一デバイス合成スレッド発行へ変更）。合成スレッドが compose.complete 直後に
/// 同じ context で保持テクスチャへ CopyResource し、Spout worker は専用フェンスでコピー完了を確認してから
/// mutex 取得と SendTexture だけを行う。送信失敗時は保持画像を Ready に戻し、次 tick に再送する。
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
    private readonly ID3D11Texture2D[] heldTextures;
    private readonly GpuFence senderFence;
    private readonly object gate = new();
    private readonly SpoutStageRing ring = new(2);
    private IntPtr self;
    private Mutex? mutex;
    public string ActualName { get; }

    public SpoutSender(GpuDevice gpu, OutputTrace log, string worker, string senderName, int width, int height, int mutexWaitMs = MutexWaitPolicy.MaxWaitMs)
    {
        SpoutTextureNative.EnsureVerifiedLibrary(warnOnly: true);
        this.gpu = gpu; this.log = log; this.worker = worker; this.mutexWaitMs = mutexWaitMs;
        heldTextures = new ID3D11Texture2D[ring.Capacity];
        senderFence = gpu.CreateFence();
        bool constructed = false;
        try
        {
            for (int i = 0; i < heldTextures.Length; i++) heldTextures[i] = gpu.Texture(width, height, SourceSharing.None);
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
            try { senderFence.Wait("Spout.constructor.drain"); }
            catch (GpuDeviceLostException) { }
            finally
            {
                try { if (constructed) SpoutTextureNative.Dtor(self); }
                finally
                {
                    if (self != IntPtr.Zero) Marshal.FreeHGlobal(self);
                    self = IntPtr.Zero;
                    mutex?.Dispose();
                    foreach (var texture in heldTextures) texture?.Dispose();
                    senderFence.Dispose();
                }
            }
            throw;
        }
    }

    /// <summary>合成スレッド。compose.complete の後に同一 context でコピーを発行する（Spout 無効時は呼ばない）。</summary>
    public void Stage(Surface source, ImageStamp stamp, long scheduled)
    {
        int slot;
        lock (gate)
        {
            if (SpoutOutputPolicy.DecideCopy(stamp.Id, ring.Held.Id) != SpoutCopyDecision.Copy) return;
            slot = ring.BeginStage(stamp);
            if (slot < 0)
            {
                log.Add("skip", worker, scheduled, stamp, "stage.busy", 1);
                return;
            }
        }
        try
        {
            gpu.Context.CopyResource(heldTextures[slot], source.Texture);
            log.Add("spout.stage", worker, scheduled, stamp, detail: "gpu", value: slot);
        }
        catch
        {
            lock (gate) ring.AbortStage(slot);
            throw;
        }
        lock (gate) ring.CompleteStage(slot, stamp);
    }

    /// <summary>Spout worker。段階リングの Ready を mutex 取得して SendTexture するだけ。</summary>
    public void Send(long scheduled, long nextScheduledQpc, CancellationToken cancellation)
    {
        int slot;
        ImageStamp stamp;
        lock (gate) slot = ring.TryBeginSend(out stamp);
        if (slot < 0)
        {
            log.Add("skip", worker, scheduled, ring.Held, detail: ring.Held.Id == 0 ? "send.noImage" : "send.noStage", 1);
            return;
        }
        // 段階リングからの選択を解析ツール互換の対で記録する（attempt 0 = 初回）。
        log.Record(new("send.select.start", worker, Stopwatch.GetTimestamp(), scheduled, stamp.Id, stamp.GeneratedQpc, Value: 0));
        log.Record(new("send.select.end", worker, Stopwatch.GetTimestamp(), scheduled, stamp.Id, stamp.GeneratedQpc, "latest", Value: 0));
        bool sent = false;
        try
        {
            // コピーは合成スレッドが同じ context に発行済み。専用フェンスの完了待ちで同一順序を確定する。
            senderFence.Wait("spout.waitStage");
            using var acquisition = SendMutexGate.Acquire(mutexWaitMs, nextScheduledQpc, Stopwatch.Frequency, cancellation,
                Stopwatch.GetTimestamp, timeout => mutex!.WaitOne(timeout), () => mutex!.ReleaseMutex(),
                attempt =>
                {
                    log.Record(new("send.acquire.start", worker, attempt.StartQpc, scheduled, stamp.Id, stamp.GeneratedQpc, Value: attempt.TimeoutMs, DeadlineQpc: attempt.DeadlineQpc));
                    log.Record(new("send.acquire.end", worker, attempt.EndQpc, scheduled, stamp.Id, stamp.GeneratedQpc, attempt.Outcome, DeadlineQpc: attempt.DeadlineQpc));
                }, reason => log.Add("skip", worker, scheduled, stamp, reason, 1));
            if (acquisition == null) return;
            string? skipReason = MutexWaitPolicy.SkipReason(Stopwatch.GetTimestamp(), nextScheduledQpc, cancellation.IsCancellationRequested);
            if (skipReason != null) { log.Add("skip", worker, scheduled, stamp, skipReason, 1); return; }
            bool submitted = false, completed = false;
            try
            {
                bool ok;
                long sendStarted = Stopwatch.GetTimestamp(), sendReturned;
                skipReason = MutexWaitPolicy.SkipReason(sendStarted, nextScheduledQpc, cancellation.IsCancellationRequested);
                if (skipReason != null) { log.Add("skip", worker, scheduled, stamp, skipReason, 1); return; }
                try
                {
                    // 最終期限確認とネイティブ呼び出しの間に記録処理を挟まない。
                    submitted = true;
                    ok = SpoutTextureNative.SendTexture(self, heldTextures[slot].NativePointer);
                    sendReturned = Stopwatch.GetTimestamp();
                }
                finally { log.Record(new("send.start", worker, sendStarted, scheduled, stamp.Id, stamp.GeneratedQpc)); }
                log.Record(new("send.return", worker, sendReturned, scheduled, stamp.Id, stamp.GeneratedQpc, ok ? "true" : "false"));
                senderFence.Wait("sender.send");
                completed = true;
                log.Add("send.gpuComplete", worker, scheduled, stamp);
                if (!ok) throw new InvalidOperationException("Spout SendTexture returned false.");
                acquisition.Dispose();
                sent = true;
                log.Add("send.publish", worker, scheduled, stamp);
            }
            finally
            {
                if (submitted && !completed) senderFence.Wait("sender.send.drain");
            }
        }
        finally
        {
            lock (gate) ring.EndSend(slot, stamp, sent);
        }
    }

    public void Dispose()
    {
        if (self != IntPtr.Zero)
        {
            SpoutTextureNative.ReleaseSender(self); SpoutTextureNative.Dtor(self); Marshal.FreeHGlobal(self); self = IntPtr.Zero;
        }
        mutex?.Dispose(); mutex = null;
        foreach (var texture in heldTextures) texture?.Dispose();
        senderFence.Dispose();
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
