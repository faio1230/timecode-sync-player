using Serilog;
using TimecodeSyncPlayer.Contracts;
using Vortice.Direct3D11;

namespace TimecodeSyncPlayer.Output;

/// <summary>shim のリース API のうち、ソース契約に必要な部分。Slot=-1 は旧サンプル経路。</summary>
internal readonly record struct GstLeaseFrameInfo(ulong Generation, ulong Sequence, long PtsNs, int Width, int Height, bool IsGpu, int Slot = -1);

/// <summary>shim の配信トレース集計（問題 H）。replaced は latest 置換回数。</summary>
internal readonly record struct GstDeliveryStatsInfo(ulong Arrivals, ulong LatestReplaced, ulong QosEvents, ulong DecoderOut, ulong RingDropped);

/// <summary>ステージ 6b: shim の共有リング記述。ハンドルは shim 所有。</summary>
internal readonly record struct GstRingInfo(int Width, int Height, IntPtr FenceHandle, IntPtr[] TextureHandles);

internal interface IGstLeasePlayer
{
    ulong Generation { get; }
    void SetGeneration(ulong generation);
    /// <summary>1 = frame, 0 = none, -6 = Ended, その他負 = error。</summary>
    int Acquire(ulong generation, out GstLeaseFrameInfo info);
    bool TryGetLeasedTexture(out IntPtr texture, out uint subresource, out uint dxgiFormat);
    void Release();
    string DecoderName { get; }
    GstDeliveryStatsInfo DeliveryStats { get; }

    /// <summary>共有リングが準備できていればそのハンドル集合を返す（未作成は false）。</summary>
    bool TryGetRingInfo(out GstRingInfo info);
}

/// <summary>
/// tcs_gstreamer.dll のリース API（acquire / leased_texture / release）を IVideoSource へ適合させる薄いアダプター。
/// shim の差を吸収する:
/// - position は受け取らず「現世代の latest 1 枚」を返すため、返却画像の pts を Stamp.PositionSeconds とする。
/// - リース保持中の acquire は同じ画像を返すため、参照カウント付きの共有リースとして同一 Stamp を返す。
/// - Ended は shim の TCS_ERR_ENDED を SourceStatus.Ended として返し、保持は合成層に任せる。
/// ステージ 6b: shim は合成デバイスを Adopt せず同一アダプター LUID の別デバイスを作る。
/// slot >= 0 のリースは共有リング（NT ハンドル + 共有フェンス）で渡り、このクラスが
/// 合成デバイス上に一度だけ開いて保持する。描画前のフェンス待ちは OutputEngine が GPU キューへ出す。
/// </summary>
internal sealed class GStreamerSource : IVideoSource
{
    private readonly IGstLeasePlayer player;
    private readonly string gpu;
    private readonly Action? onRingOpened;
    private GpuDevice? device;
    private SharedLease? active;
    private RingResources? ring;
    private bool ringOpenFailedLogged;
    private long notReady, ready, generationRejected;
    private int peakLeases;
    private long ringOutsideFrames;
    private bool ringOutsideLogged;

    public GStreamerSource(IGstLeasePlayer player, string gpu = "", GpuDevice? device = null, Action? onRingOpened = null)
    {
        this.player = player;
        this.gpu = gpu;
        this.device = device;
        this.onRingOpened = onRingOpened;
    }

    /// <summary>shim が保持する現在世代（合成層の generation と対応付ける）。</summary>
    public int Generation => (int)player.Generation;

    public void SetGeneration(int generation)
    {
        if ((int)player.Generation == generation) return;
        player.SetGeneration((ulong)generation);
        // 旧世代の画像は返さない。shim は latest を破棄し、リース中の画像は release まで保持される。
        if (active != null) generationRejected++;
    }

    public SourceStatus TryAcquire(int generation, double positionSeconds, out ISourceImageLease? lease)
    {
        lease = null;
        if ((int)player.Generation != generation) { notReady++; return SourceStatus.NotReady; }
        if (active != null)
        {
            if ((int)active.Info.Generation != generation) { notReady++; return SourceStatus.NotReady; }
            lease = active.Retain();
            return SourceStatus.Ready;
        }
        int acquired = player.Acquire((ulong)generation, out GstLeaseFrameInfo info);
        if (acquired == TimecodeSyncPlayer.Gst.GstNative.TcsErrEnded) return SourceStatus.Ended;
        if (acquired != 1) { notReady++; return SourceStatus.NotReady; }
        if (info.Generation != (ulong)generation || info.Width <= 0 || info.Height <= 0)
        {
            player.Release();
            notReady++;
            return SourceStatus.NotReady;
        }
        IntPtr texture;
        // slot<0（旧サンプル経路）ではリングを開きに行かない。既に開いていれば寸法をログに使うだけ。
        RingResources? resources = info.Slot >= 0 ? EnsureRing() : ring;
        if (GstRingPolicy.Decide(info.Slot, resources?.Count ?? 0, resources != null) != GstRingLeasePlan.UseRing)
        {
            if (info.Slot < 0)
                RecordRingOutsideFrame(info);
            player.Release();
            notReady++;
            return SourceStatus.NotReady;
        }
        texture = resources!.Textures[info.Slot].NativePointer;
        active = new SharedLease(this, info, texture, positionSeconds);
        ready++;
        peakLeases = Math.Max(peakLeases, active.References);
        lease = active.Retain();
        return SourceStatus.Ready;
    }

    public SourceDiagnostics Diagnostics
    {
        get
        {
            GstDeliveryStatsInfo stats = player.DeliveryStats;
            // Replaced は shim の latest 置換回数（問題 H の計測）。
            return new(player.DecoderName, gpu, "BGRA8_UNORM", generationRejected, notReady,
                (long)stats.LatestReplaced, 0, peakLeases, ready + notReady, ready, 0);
        }
    }

    public bool TryDispose() => active == null;

    /// <summary>D8: リング外（旧サンプル経路）で返ってきたフレーム数。2 秒ごとの統計に出す。</summary>
    internal long RingOutsideFrames => Interlocked.Read(ref ringOutsideFrames);

    /// <summary>
    /// D8: shim が解像度不一致などでリング外のリースを返したときの記録。
    /// 最初の 1 回だけ警告し、以後は <see cref="RingOutsideFrames"/> に数えるだけ。
    /// </summary>
    private void RecordRingOutsideFrame(GstLeaseFrameInfo info)
    {
        Interlocked.Increment(ref ringOutsideFrames);
        if (ringOutsideLogged) return;
        ringOutsideLogged = true;
        RingResources? resources = ring;
        string ringSize = resources == null ? "未接続" : $"{resources.Width}x{resources.Height}";
        Log.Warning("GStreamerSource: リング外のフレームを受け取りました {W}x{H}（リングは {Ring}）。GPU 合成では使いません",
            info.Width, info.Height, ringSize);
    }

    /// <summary>
    /// 段階 5.2: デバイス消失後の再オープン。shim は別デバイスなので player は destroy しない。
    /// 合成側のリング Surface・共有フェンスを新デバイスで開き直す。開き直せなければ false（player 再生成へ）。
    /// </summary>
    internal bool TryReopenOn(GpuDevice newDevice)
    {
        if (active != null) return false;
        ring?.Dispose();
        ring = null;
        ringOpenFailedLogged = false;
        device = newDevice;
        return EnsureRing() != null;
    }

    /// <summary>復旧の第 1 段: 旧デバイス上のリング資源を手放す（player は触らない）。</summary>
    internal void DropRingResourcesForRecovery()
    {
        ring?.Dispose();
        ring = null;
        if (active != null)
        {
            active = null;
            player.Release();
        }
    }

    public void Dispose()
    {
        if (!TryDispose()) throw new InvalidOperationException("GStreamerSource: a lease is still outstanding; release it before disposing.");
        // 共有リングのリソースは、worker 停止・GPU ドレイン後にここで解放する。
        ring?.Dispose();
        ring = null;
    }

    /// <summary>slot のリング Surface（SRV + テクスチャ）を返す。owner は GStreamerSource。</summary>
    internal bool TryGetRingSurface(int slot, out ID3D11Texture2D? texture, out ID3D11ShaderResourceView? view)
    {
        texture = null;
        view = null;
        RingResources? resources = ring;
        if (resources == null || GstRingPolicy.Decide(slot, resources.Count, true) != GstRingLeasePlan.UseRing)
            return false;
        texture = resources.Textures[slot];
        view = resources.Views[slot];
        return true;
    }

    /// <summary>描画前の GPU キュー待ち（CPU は待たない）。slot>=0 のリースでのみ使う。</summary>
    internal void WaitRingFence(ulong value)
    {
        if (device == null || ring == null) return;
        device.Context4.Wait(ring.Fence, value);
    }

    /// <summary>
    /// I1/I5: リング slot のコピー完了（フェンス値＝seq）を CPU 側で確認する。
    /// 完了前のフレームを合成の GPU フェンス待ちに含めないための専用クエリ。
    /// </summary>
    internal bool IsRingFenceComplete(long sequence) => ring != null && ring.Fence.CompletedValue >= (ulong)sequence;

    /// <summary>合成デバイス上にリングを一度だけ開く（未作成/未接続は null で毎 tick 再試行）。</summary>
    private RingResources? EnsureRing()
    {
        if (ring != null) return ring;
        if (device == null) return null;
        if (!player.TryGetRingInfo(out GstRingInfo info)) return null;
        try
        {
            ring = RingResources.Open(device, info);
        }
        catch (Exception ex)
        {
            if (!ringOpenFailedLogged)
            {
                ringOpenFailedLogged = true;
                Log.Warning(ex, "GStreamerSource: 共有リングのオープンに失敗（旧経路へフォールバック、再試行は継続）");
            }
            return null;
        }
        if (ring != null)
        {
            Log.Information("GStreamerSource: 共有リングを開きました {W}x{H} slots={Count}", ring.Width, ring.Height, ring.Count);
            onRingOpened?.Invoke();
        }
        return ring;
    }

    private void ReleaseShared(SharedLease shared)
    {
        if (ReferenceEquals(active, shared)) active = null;
        player.Release();
    }

    /// <summary>合成デバイスが開いたリング 3 面 + 共有フェンス。GStreamerSource が所有する。</summary>
    private sealed class RingResources : IDisposable
    {
        public ID3D11Texture2D[] Textures { get; }
        public ID3D11ShaderResourceView[] Views { get; }
        public ID3D11Fence Fence { get; }
        public int Width { get; }
        public int Height { get; }
        public int Count => Textures.Length;

        private RingResources(ID3D11Texture2D[] textures, ID3D11ShaderResourceView[] views,
            ID3D11Fence fence, int width, int height)
        {
            Textures = textures;
            Views = views;
            Fence = fence;
            Width = width;
            Height = height;
        }

        public static RingResources? Open(GpuDevice gpu, GstRingInfo info)
        {
            if (info.TextureHandles.Length == 0 || info.FenceHandle == IntPtr.Zero) return null;
            var textures = new ID3D11Texture2D[info.TextureHandles.Length];
            var views = new ID3D11ShaderResourceView[textures.Length];
            ID3D11Fence? fence = null;
            try
            {
                fence = gpu.Device5.OpenSharedFence<ID3D11Fence>(info.FenceHandle);
                for (int i = 0; i < textures.Length; i++)
                {
                    textures[i] = gpu.Device1.OpenSharedResource1<ID3D11Texture2D>(info.TextureHandles[i]);
                    views[i] = gpu.Device.CreateShaderResourceView(textures[i]);
                }
                return new RingResources(textures, views, fence, info.Width, info.Height);
            }
            catch
            {
                fence?.Dispose();
                for (int i = 0; i < textures.Length; i++)
                {
                    views[i]?.Dispose();
                    textures[i]?.Dispose();
                }
                throw;
            }
        }

        public void Dispose()
        {
            foreach (ID3D11ShaderResourceView view in Views) view.Dispose();
            foreach (ID3D11Texture2D texture in Textures) texture.Dispose();
            Fence.Dispose();
        }
    }

    /// <summary>shim の1リースを複数の利用者へ共有する参照カウント holder。最後の Dispose で shim へ返す。</summary>
    internal sealed class SharedLease
    {
        private readonly GStreamerSource owner;
        private int references;
        public GstLeaseFrameInfo Info { get; }
        public IntPtr TexturePointer { get; }
        public double FallbackPositionSeconds { get; }
        public int References => Volatile.Read(ref references);

        public SharedLease(GStreamerSource owner, GstLeaseFrameInfo info, IntPtr texture, double positionSeconds)
        {
            this.owner = owner;
            Info = info;
            TexturePointer = texture;
            FallbackPositionSeconds = positionSeconds;
        }

        public Lease Retain()
        {
            int count = Interlocked.Increment(ref references);
            owner.peakLeases = Math.Max(owner.peakLeases, count);
            return new Lease(this);
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref references) != 0) return;
            owner.ReleaseShared(this);
        }
    }

    public sealed class Lease : ISourceImageLease
    {
        private readonly SharedLease shared;
        private bool inFlight, disposed;
        internal Lease(SharedLease shared) { this.shared = shared; }

        public SourceImageStamp Stamp => new(
            (int)shared.Info.Generation,
            (long)shared.Info.Sequence,
            shared.Info.PtsNs >= 0 ? shared.Info.PtsNs / 1_000_000_000.0 : shared.FallbackPositionSeconds,
            shared.Info.PtsNs >= 0 ? shared.Info.PtsNs / 100 : 0);

        // AddRef して所有権を取ったラッパーを返す（呼び出し側が Dispose する）。
        public Vortice.Direct3D11.ID3D11Texture2D? Texture => OpenTexture();

        internal IntPtr TexturePointer => shared.TexturePointer;

        /// <summary>ステージ 6b: 共有リング slot（-1 = 旧サンプル経路）。描画前のフェンス待ちに使う。</summary>
        internal int Slot => shared.Info.Slot;

        /// <summary>リース中のテクスチャを AddRef 付きの所有ラッパーとして開く（lease 保持中のみ有効）。</summary>
        public Vortice.Direct3D11.ID3D11Texture2D? OpenTexture()
            => shared.TexturePointer == IntPtr.Zero
                ? null
                : NativeTextureOps.OpenOwned(shared.TexturePointer, pointer => new Vortice.Direct3D11.ID3D11Texture2D(pointer));

        public int Width => shared.Info.Width;
        public int Height => shared.Info.Height;
        public SourceImageFormat Format => SourceImageFormat.Bgra8;

        public void BeginGpuUse()
        {
            if (disposed || inFlight) throw new InvalidOperationException("Lease is returned or already in GPU use.");
            inFlight = true;
        }

        public void CompleteGpuUse()
        {
            if (disposed || !inFlight) throw new InvalidOperationException("No active GPU use.");
            inFlight = false;
        }

        public void Dispose()
        {
            if (inFlight) throw new InvalidOperationException("GPU use must complete before lease return.");
            if (disposed) return;
            disposed = true;
            shared.Release();
        }
    }
}

/// <summary>IGstNativeApi + player ハンドルを IGstLeasePlayer へ適合させる（将来の本体配線用）。</summary>
internal sealed class GstNativeLeasePlayer(TimecodeSyncPlayer.Gst.IGstNativeApi native, IntPtr player) : IGstLeasePlayer
{
    public ulong Generation => native.GetGeneration(player);
    public void SetGeneration(ulong generation) => native.SetGeneration(player, generation);

    public int Acquire(ulong generation, out GstLeaseFrameInfo info)
    {
        int code = native.Acquire(player, generation, out TimecodeSyncPlayer.Gst.GstNative.TcsFrameInfo frame);
        if (code != 1)
        {
            info = default;
            return code;
        }
        info = new GstLeaseFrameInfo(frame.Generation, frame.Seq, frame.PtsNs, frame.Width, frame.Height, frame.IsGpu != 0, frame.Slot);
        return 1;
    }

    public bool TryGetLeasedTexture(out IntPtr texture, out uint subresource, out uint dxgiFormat)
        => native.TryGetLeasedTexture(player, out texture, out subresource, out dxgiFormat);

    public void Release() => native.Release(player);
    public string DecoderName => native.DecoderName(player);

    public GstDeliveryStatsInfo DeliveryStats
    {
        get
        {
            if (native.GetDeliveryStats(player, out TimecodeSyncPlayer.Gst.GstNative.TcsDeliveryStats stats) != 0)
                return default;
            return new(stats.Arrivals, stats.LatestReplaced, stats.QosEvents, stats.DecoderOut, stats.RingDropped);
        }
    }

    public bool TryGetRingInfo(out GstRingInfo info)
    {
        var handles = new IntPtr[4];
        if (native.GetRingInfo(player, handles, (uint)handles.Length, out uint count,
                out IntPtr fence, out int width, out int height) != 0
            || count == 0 || count > handles.Length || fence == IntPtr.Zero)
        {
            info = default;
            return false;
        }
        if (count != handles.Length)
            Array.Resize(ref handles, (int)count);
        info = new GstRingInfo(width, height, fence, handles);
        return true;
    }
}
