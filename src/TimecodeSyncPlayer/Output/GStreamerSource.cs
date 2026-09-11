using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Output;

/// <summary>shim のリース API のうち、ソース契約に必要な部分。</summary>
internal readonly record struct GstLeaseFrameInfo(ulong Generation, ulong Sequence, long PtsNs, int Width, int Height, bool IsGpu);

internal interface IGstLeasePlayer
{
    ulong Generation { get; }
    void SetGeneration(ulong generation);
    bool Acquire(ulong generation, out GstLeaseFrameInfo info);
    bool TryGetLeasedTexture(out IntPtr texture, out uint subresource, out uint dxgiFormat);
    void Release();
    string DecoderName { get; }
}

/// <summary>
/// tcs_gstreamer.dll のリース API（acquire / leased_texture / release）を IVideoSource へ適合させる薄いアダプター。
/// shim の差を吸収する:
/// - position は受け取らず「現世代の latest 1 枚」を返すため、返却画像の pts を Stamp.PositionSeconds とする。
/// - リース保持中の acquire は同じ画像を返すため、参照カウント付きの共有リースとして同一 Stamp を返す。
/// - Ended は区別できない（0 = なし）ため NotReady とし、保持は合成層に任せる。
/// デバイスは OutputEngine のものを shim に Adopt させる前提（別デバイス共有は行わない）。
/// </summary>
internal sealed class GStreamerSource : IVideoSource
{
    private readonly IGstLeasePlayer player;
    private readonly string gpu;
    private SharedLease? active;
    private long notReady, ready, generationRejected;
    private int peakLeases;

    public GStreamerSource(IGstLeasePlayer player, string gpu = "")
    {
        this.player = player;
        this.gpu = gpu;
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
        if (!player.Acquire((ulong)generation, out GstLeaseFrameInfo info)) { notReady++; return SourceStatus.NotReady; }
        if (info.Generation != (ulong)generation || info.Width <= 0 || info.Height <= 0
            || !player.TryGetLeasedTexture(out IntPtr texture, out _, out uint dxgiFormat) || dxgiFormat != 87)
        {
            player.Release();
            notReady++;
            return SourceStatus.NotReady;
        }
        active = new SharedLease(this, info, texture, positionSeconds);
        ready++;
        peakLeases = Math.Max(peakLeases, active.References);
        lease = active.Retain();
        return SourceStatus.Ready;
    }

    public SourceDiagnostics Diagnostics => new(
        player.DecoderName, gpu, "BGRA8_UNORM", generationRejected, notReady, 0, 0, peakLeases, ready + notReady, ready, 0);

    public bool TryDispose() => active == null;

    public void Dispose()
    {
        if (!TryDispose()) throw new InvalidOperationException("GStreamerSource: a lease is still outstanding; release it before disposing.");
    }

    private void ReleaseShared(SharedLease shared)
    {
        if (ReferenceEquals(active, shared)) active = null;
        player.Release();
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

    public bool Acquire(ulong generation, out GstLeaseFrameInfo info)
    {
        if (!native.Acquire(player, generation, out TimecodeSyncPlayer.Gst.GstNative.TcsFrameInfo frame))
        {
            info = default;
            return false;
        }
        info = new GstLeaseFrameInfo(frame.Generation, frame.Seq, frame.PtsNs, frame.Width, frame.Height, frame.IsGpu != 0);
        return true;
    }

    public bool TryGetLeasedTexture(out IntPtr texture, out uint subresource, out uint dxgiFormat)
        => native.TryGetLeasedTexture(player, out texture, out subresource, out dxgiFormat);

    public void Release() => native.Release(player);
    public string DecoderName => native.DecoderName(player);
}
