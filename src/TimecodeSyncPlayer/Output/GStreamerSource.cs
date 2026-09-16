using Serilog;
using TimecodeSyncPlayer.Contracts;
using Vortice.Direct3D11;

namespace TimecodeSyncPlayer.Output;

/// <summary>shim のリース API のうち、ソース契約に必要な部分。Slot=-1 は旧サンプル経路。</summary>
internal readonly record struct GstLeaseFrameInfo(ulong Generation, ulong Sequence, long PtsNs, int Width, int Height, bool IsGpu, int Slot = -1, uint RingEpoch = 0);

/// <summary>shim の配信トレース集計（問題 H）。replaced は latest 置換回数。</summary>
internal readonly record struct GstDeliveryStatsInfo(ulong Arrivals, ulong LatestReplaced, ulong QosEvents, ulong DecoderOut, ulong RingDropped);

/// <summary>ステージ 6b: shim の共有リング記述。ハンドルは shim 所有。Epoch は D8 のリング世代。</summary>
internal readonly record struct GstRingInfo(int Width, int Height, IntPtr FenceHandle, IntPtr[] TextureHandles, uint Epoch = 0);

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

/// <summary>D8: 合成デバイス上に開いたリング資源の抽象（テストでは実 D3D 無しの偽物を差し込む）。</summary>
internal interface IGstRingResources : IDisposable
{
    int Width { get; }
    int Height { get; }
    int Count { get; }
    uint Epoch { get; }
    IntPtr TexturePointer(int slot);
    bool TryGetSurface(int slot, out ID3D11Texture2D? texture, out ID3D11ShaderResourceView? view);
    bool IsFenceComplete(ulong value);
    void WaitFence(ID3D11DeviceContext4 context, ulong value);
    /// <summary>このリングを参照するリースを 1 本増やす。</summary>
    void AddLeaseReference();
    /// <summary>リースを 1 本返す。参照が 0 になったら破棄する。</summary>
    void ReleaseLeaseReference();
    /// <summary>現行リングから降ろす。参照が 0 になったら破棄する（未返却リースが残っていれば後で）。</summary>
    void ReleaseCurrentReference();
}

/// <summary>D8: リング資源の生成口。製品実装は GpuDevice で開き、テストは偽物を返す。</summary>
internal interface IGstRingResourcesFactory
{
    IGstRingResources? Open(GstRingInfo info, GpuDevice? device);
}

/// <summary>
/// D8: リング資源の寿命。参照数 = 未返却リース数 + (現行なら 1)。
/// 解像度が変わって現行から降ろしても、旧リングのリース（Held を含む）が
/// 全部返るまで Surface / フェンスを破棄しない。
/// </summary>
internal abstract class RingResourcesLifetime : IGstRingResources
{
    private int references = 1;
    private int disposed;

    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    public abstract int Width { get; }
    public abstract int Height { get; }
    public abstract int Count { get; }
    public abstract uint Epoch { get; }
    public abstract IntPtr TexturePointer(int slot);
    public abstract bool TryGetSurface(int slot, out ID3D11Texture2D? texture, out ID3D11ShaderResourceView? view);
    public abstract bool IsFenceComplete(ulong value);
    public abstract void WaitFence(ID3D11DeviceContext4 context, ulong value);

    public void AddLeaseReference() => Interlocked.Increment(ref references);

    public void ReleaseLeaseReference()
    {
        if (Interlocked.Decrement(ref references) == 0)
            Dispose();
    }

    public void ReleaseCurrentReference()
    {
        if (Interlocked.Decrement(ref references) == 0)
            Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        DisposeCore();
    }

    protected abstract void DisposeCore();
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
    private readonly Action? onEnded;
    private readonly IGstRingResourcesFactory ringFactory;
    private GpuDevice? device;
    private SharedLease? active;
    private IGstRingResources? ring;
    private bool ringOpenFailedLogged;
    private long notReady, ready, generationRejected;
    private int peakLeases;
    private long ringOutsideFrames;
    private bool ringOutsideLogged;

    public GStreamerSource(IGstLeasePlayer player, string gpu = "", GpuDevice? device = null, Action? onRingOpened = null,
        IGstRingResourcesFactory? ringFactory = null, Action? onEnded = null)
    {
        this.player = player;
        this.gpu = gpu;
        this.device = device;
        this.onRingOpened = onRingOpened;
        this.ringFactory = ringFactory ?? new GpuRingResourcesFactory();
        this.onEnded = onEnded;
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
        if (acquired == TimecodeSyncPlayer.Gst.GstNative.TcsErrEnded)
        {
            // D11: EOF を観測したらシーク保留を解除する（EOF 後は新しい配信が来ない）。
            onEnded?.Invoke();
            return SourceStatus.Ended;
        }
        if (acquired != 1) { notReady++; return SourceStatus.NotReady; }
        if (info.Generation != (ulong)generation || info.Width <= 0 || info.Height <= 0)
        {
            player.Release();
            notReady++;
            return SourceStatus.NotReady;
        }
        if (info.Slot < 0)
        {
            // D8: 旧サンプル経路のテクスチャは shim デバイスの非共有資源で、合成デバイスでは描けない。
            RecordRingOutsideFrame(info);
            player.Release();
            notReady++;
            return SourceStatus.NotReady;
        }
        // D8: リースの epoch が現行リングと違えば（解像度変更で shim が作り直した）開き直す。
        IGstRingResources? resources = EnsureRing(info.RingEpoch);
        if (resources == null || GstRingPolicy.Decide(info.Slot, resources.Count, true) != GstRingLeasePlan.UseRing)
        {
            player.Release();
            notReady++;
            return SourceStatus.NotReady;
        }
        resources.AddLeaseReference();
        active = new SharedLease(this, info, resources.TexturePointer(info.Slot), positionSeconds, resources);
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
        IGstRingResources? resources = ring;
        string ringSize = resources == null ? "未接続" : $"{resources.Width}x{resources.Height}（epoch {resources.Epoch}）";
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
        RetireCurrentRing();
        ringOpenFailedLogged = false;
        device = newDevice;
        return EnsureRing(requiredEpoch: null) != null;
    }

    /// <summary>復旧の第 1 段: 旧デバイス上のリング資源を手放す（player は触らない）。</summary>
    internal void DropRingResourcesForRecovery()
    {
        RetireCurrentRing();
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
        RetireCurrentRing();
    }

    /// <summary>現行リングを降ろす。未返却リースが残っていれば最後の返却時に破棄される（D8）。</summary>
    private void RetireCurrentRing()
    {
        IGstRingResources? previous = ring;
        ring = null;
        previous?.ReleaseCurrentReference();
    }

    /// <summary>slot のリング Surface（SRV + テクスチャ）を返す。owner は GStreamerSource。</summary>
    internal bool TryGetRingSurface(int slot, out ID3D11Texture2D? texture, out ID3D11ShaderResourceView? view)
    {
        texture = null;
        view = null;
        // D8: 描いているリースの世代のリングを使う（現行リングとは限らない）。
        IGstRingResources? resources = active?.Ring ?? ring;
        return resources != null && resources.TryGetSurface(slot, out texture, out view);
    }

    /// <summary>描画前の GPU キュー待ち（CPU は待たない）。slot>=0 のリースでのみ使う。</summary>
    internal void WaitRingFence(ulong value)
    {
        IGstRingResources? resources = active?.Ring ?? ring;
        if (device == null || resources == null) return;
        resources.WaitFence(device.Context4, value);
    }

    /// <summary>
    /// I1/I5: リング slot のコピー完了（フェンス値＝seq）を CPU 側で確認する。
    /// 完了前のフレームを合成の GPU フェンス待ちに含めないための専用クエリ。
    /// </summary>
    internal bool IsRingFenceComplete(long sequence)
    {
        IGstRingResources? resources = active?.Ring ?? ring;
        return resources != null && resources.IsFenceComplete((ulong)sequence);
    }

    /// <summary>
    /// 合成デバイス上にリングを開く（未作成/未接続/世代不一致は null で毎 tick 再試行）。
    /// D8: 解像度変更で shim がリングを作り直すと epoch が変わるため開き直す。
    /// 旧リングは、それを参照するリース（Held を含む）が返るまで破棄しない。
    /// </summary>
    private IGstRingResources? EnsureRing(uint? requiredEpoch)
    {
        if (ring != null && (!requiredEpoch.HasValue || ring.Epoch == requiredEpoch.Value))
            return ring;
        if (!player.TryGetRingInfo(out GstRingInfo info)) return null;
        if (requiredEpoch.HasValue && info.Epoch != requiredEpoch.Value) return null;
        IGstRingResources? opened;
        try
        {
            opened = ringFactory.Open(info, device);
        }
        catch (Exception ex)
        {
            if (!ringOpenFailedLogged)
            {
                ringOpenFailedLogged = true;
                Log.Warning(ex, "GStreamerSource: 共有リングのオープンに失敗（再試行は継続）");
            }
            return null;
        }
        if (opened == null) return null;
        ringOpenFailedLogged = false;
        IGstRingResources? previous = ring;
        ring = opened;
        previous?.ReleaseCurrentReference();
        if (previous != null)
            Log.Information("GStreamerSource: 共有リングを開き直しました {W}x{H} epoch={Epoch}", opened.Width, opened.Height, opened.Epoch);
        else
            Log.Information("GStreamerSource: 共有リングを開きました {W}x{H} slots={Count} epoch={Epoch}", opened.Width, opened.Height, opened.Count, opened.Epoch);
        onRingOpened?.Invoke();
        return ring;
    }

    private void ReleaseShared(SharedLease shared)
    {
        if (ReferenceEquals(active, shared)) active = null;
        player.Release();
        shared.Ring?.ReleaseLeaseReference();
    }

    /// <summary>合成デバイスが開いたリング 3 面 + 共有フェンス（D8: 世代付き）。</summary>
    private sealed class GpuRingResources : RingResourcesLifetime
    {
        private readonly ID3D11Texture2D[] textures;
        private readonly ID3D11ShaderResourceView[] views;
        private readonly ID3D11Fence fence;

        public override int Width { get; }
        public override int Height { get; }
        public override int Count => textures.Length;
        public override uint Epoch { get; }

        private GpuRingResources(ID3D11Texture2D[] textures, ID3D11ShaderResourceView[] views,
            ID3D11Fence fence, int width, int height, uint epoch)
        {
            this.textures = textures;
            this.views = views;
            this.fence = fence;
            Width = width;
            Height = height;
            Epoch = epoch;
        }

        public static GpuRingResources? Open(GpuDevice? gpu, GstRingInfo info)
        {
            if (gpu == null) return null;
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
                return new GpuRingResources(textures, views, fence, info.Width, info.Height, info.Epoch);
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

        public override IntPtr TexturePointer(int slot) => textures[slot].NativePointer;

        public override bool TryGetSurface(int slot, out ID3D11Texture2D? texture, out ID3D11ShaderResourceView? view)
        {
            texture = null;
            view = null;
            if (slot < 0 || slot >= textures.Length) return false;
            texture = textures[slot];
            view = views[slot];
            return true;
        }

        public override bool IsFenceComplete(ulong value) => fence.CompletedValue >= value;

        public override void WaitFence(ID3D11DeviceContext4 context, ulong value) => context.Wait(fence, value);

        protected override void DisposeCore()
        {
            foreach (ID3D11ShaderResourceView view in views) view.Dispose();
            foreach (ID3D11Texture2D texture in textures) texture.Dispose();
            fence.Dispose();
        }
    }

    /// <summary>製品経路: GpuDevice 上に NT ハンドルを開く。</summary>
    private sealed class GpuRingResourcesFactory : IGstRingResourcesFactory
    {
        public IGstRingResources? Open(GstRingInfo info, GpuDevice? device) => GpuRingResources.Open(device, info);
    }

    /// <summary>shim の1リースを複数の利用者へ共有する参照カウント holder。最後の Dispose で shim へ返す。</summary>
    internal sealed class SharedLease
    {
        private readonly GStreamerSource owner;
        private int references;
        public GstLeaseFrameInfo Info { get; }
        public IntPtr TexturePointer { get; }
        public double FallbackPositionSeconds { get; }
        /// <summary>D8: このリースが参照するリング世代。最後の Dispose で参照を返す。</summary>
        public IGstRingResources? Ring { get; }
        public int References => Volatile.Read(ref references);

        public SharedLease(GStreamerSource owner, GstLeaseFrameInfo info, IntPtr texture, double positionSeconds,
            IGstRingResources? ring = null)
        {
            this.owner = owner;
            Info = info;
            TexturePointer = texture;
            FallbackPositionSeconds = positionSeconds;
            Ring = ring;
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
        info = new GstLeaseFrameInfo(frame.Generation, frame.Seq, frame.PtsNs, frame.Width, frame.Height, frame.IsGpu != 0, frame.Slot, frame.RingEpoch);
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
        uint epoch = 0;
        _ = native.GetRingEpoch(player, out epoch);
        info = new GstRingInfo(width, height, fence, handles, epoch);
        return true;
    }
}
