using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;
using Vortice.Direct3D11;

namespace TimecodeSyncPlayer.Tests;

public class GStreamerSourceTests
{
    private sealed class FakeLeasePlayer : IGstLeasePlayer
    {
        public ulong Gen = 1;
        public bool HasFrame = true;
        public ulong NextSequence = 1;
        public int NextSlot = -1;
        public uint NextRingEpoch = 1;
        public bool HasRing;
        public GstRingInfo RingInfo = new(1920, 1080, new IntPtr(9), [new IntPtr(1), new IntPtr(2), new IntPtr(3)], 1);
        public int Releases;
        public int Acquires;

        public ulong Generation => Gen;
        public void SetGeneration(ulong generation) { Gen = generation; HasFrame = false; }
        public bool Ended;
        public int Acquire(ulong generation, out GstLeaseFrameInfo info)
        {
            if (Ended) { info = default; return -6; }
            if (!HasFrame || generation != Gen) { info = default; return 0; }
            info = new GstLeaseFrameInfo(Gen, NextSequence, (long)((NextSequence - 1) * 0.04 * 1_000_000_000), 1920, 1080, true, NextSlot, NextRingEpoch);
            Acquires++;
            return 1;
        }
        public bool TryGetLeasedTexture(out IntPtr texture, out uint subresource, out uint dxgiFormat)
        {
            texture = IntPtr.Zero;
            subresource = 0;
            dxgiFormat = 87;
            return true;
        }
        public bool TryGetRingInfo(out GstRingInfo info)
        {
            info = RingInfo;
            return HasRing;
        }
        public void Release() { Releases++; NextSequence++; }
        public string DecoderName => "d3d11h264dec";
        public GstDeliveryStatsInfo DeliveryStats { get; set; }
    }

    /// <summary>D8 seam: 実 GpuDevice 無しでリング資源を差し替える。</summary>
    private sealed class FakeRing : RingResourcesLifetime
    {
        private readonly int width;
        private readonly int height;
        private readonly int count;
        private readonly uint epoch;

        public FakeRing(uint epoch, int width = 1920, int height = 1080, int count = 3)
        {
            this.epoch = epoch;
            this.width = width;
            this.height = height;
            this.count = count;
        }

        public override int Width => width;
        public override int Height => height;
        public override int Count => count;
        public override uint Epoch => epoch;
        public override IntPtr TexturePointer(int slot) => new(1000 + slot);
        public override bool TryGetSurface(int slot, out ID3D11Texture2D? texture, out ID3D11ShaderResourceView? view)
        {
            texture = null;
            view = null;
            return slot >= 0 && slot < count;
        }
        public override bool IsFenceComplete(ulong value) => true;
        public override void WaitFence(ID3D11DeviceContext4 context, ulong value) { }
        protected override void DisposeCore() { }
    }

    private sealed class FakeRingFactory : IGstRingResourcesFactory
    {
        public readonly List<FakeRing> Rings = [];
        public bool ThrowOnNextOpen;
        public IGstRingResources? Open(GstRingInfo info, GpuDevice? device)
        {
            if (ThrowOnNextOpen)
            {
                ThrowOnNextOpen = false;
                throw new InvalidOperationException("fake ring open failure");
            }
            var ring = new FakeRing(info.Epoch, info.Width, info.Height, info.TextureHandles.Length);
            Rings.Add(ring);
            return ring;
        }
    }

    [Fact]
    public void TryAcquire_MapsShimEndedToEndedStatus()
    {
        var player = new FakeLeasePlayer { HasFrame = false, Ended = true };
        var source = new GStreamerSource(player, "gpu");
        source.TryAcquire(1, 0, out var lease).Should().Be(SourceStatus.Ended);
        lease.Should().BeNull();
    }

    [Fact]
    public void SharedLease_KeepsTheShimLeaseUntilTheLastRetainIsDisposed()
    {
        var player = new FakeLeasePlayer();
        var source = new GStreamerSource(player, "gpu");
        var shared = new GStreamerSource.SharedLease(source,
            new GstLeaseFrameInfo(1, 1, 0, 1920, 1080, true, 0), IntPtr.Zero, 0);
        var first = shared.Retain();
        var second = shared.Retain();
        first.Stamp.Generation.Should().Be(1);
        first.Stamp.Sequence.Should().Be(1);
        first.Width.Should().Be(1920);
        first.Height.Should().Be(1080);
        first.Texture.Should().BeNull();
        first.Format.Should().Be(SourceImageFormat.Bgra8);

        first.Dispose();
        player.Releases.Should().Be(0);
        second.Dispose();
        player.Releases.Should().Be(1);
    }

    [Fact]
    public void TryAcquire_RefusesMismatchedGenerationAndNoFrame()
    {
        var player = new FakeLeasePlayer();
        var source = new GStreamerSource(player);
        source.TryAcquire(2, 0, out var lease).Should().Be(SourceStatus.NotReady);
        lease.Should().BeNull();
        player.Acquires.Should().Be(0);

        source.SetGeneration(2);
        source.TryAcquire(1, 0, out _).Should().Be(SourceStatus.NotReady);
        source.TryAcquire(2, 0, out _).Should().Be(SourceStatus.NotReady); // HasFrame=false after switch.
        player.Generation.Should().Be(2);
        source.TryDispose().Should().BeTrue();
    }

    [Fact]
    public void SetGeneration_MakesOlderImagesUnavailable()
    {
        var player = new FakeLeasePlayer();
        var source = new GStreamerSource(player);
        source.SetGeneration(2);
        source.TryAcquire(1, 0, out _).Should().Be(SourceStatus.NotReady);
        source.TryAcquire(2, 0, out _).Should().Be(SourceStatus.NotReady); // HasFrame=false after switch.
        player.Generation.Should().Be(2);
        source.Diagnostics.Decoder.Should().Be("d3d11h264dec");
        source.Diagnostics.Format.Should().Be("BGRA8_UNORM");
        source.TryDispose().Should().BeTrue();
    }

    [Fact]
    public void Diagnostics_MirrorsShimReplacementCount()
    {
        var player = new FakeLeasePlayer { DeliveryStats = new GstDeliveryStatsInfo(2400, 7, 3, 0, 0) };
        var source = new GStreamerSource(player);

        SourceDiagnostics diagnostics = source.Diagnostics;

        diagnostics.Replaced.Should().Be(7);
        diagnostics.Decoder.Should().Be("d3d11h264dec");
    }

    [Fact]
    public void Lease_GuardsGpuUseAndDisposesOnce()
    {
        var player = new FakeLeasePlayer();
        var source = new GStreamerSource(player);
        var shared = new GStreamerSource.SharedLease(source,
            new GstLeaseFrameInfo(1, 1, 0, 1920, 1080, true, 0), IntPtr.Zero, 0);
        var lease = shared.Retain();
        FluentActions.Invoking(() => lease.CompleteGpuUse()).Should().Throw<InvalidOperationException>();
        lease.BeginGpuUse();
        FluentActions.Invoking(() => lease.BeginGpuUse()).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => lease.Dispose()).Should().Throw<InvalidOperationException>();
        lease.CompleteGpuUse();
        lease.Dispose();
        lease.Dispose();
        player.Releases.Should().Be(1);
        source.TryDispose().Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_RingSlotWithoutComposeDevice_ReturnsNotReadyAndReleasesTheLease()
    {
        // slot >= 0 は共有リングが必須。合成デバイスが無ければ開けないので拒否し、
        // リースは shim へ返す（リークさせない）。
        var player = new FakeLeasePlayer { HasRing = true, NextSlot = 0 };
        var source = new GStreamerSource(player);
        source.TryAcquire(1, 0, out var lease).Should().Be(SourceStatus.NotReady);
        lease.Should().BeNull();
        player.Releases.Should().Be(1);
        source.TryDispose().Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_SlotMinusOne_ReturnsNotReadyAndReleasesTheLease()
    {
        // D8: リング外（旧サンプル経路）のテクスチャは shim デバイスの非共有資源で、
        // GPU 合成では描けない。リースは返して NotReady にする（合成は Held を描く）。
        var player = new FakeLeasePlayer();
        var source = new GStreamerSource(player);
        source.TryAcquire(1, 0, out var lease).Should().Be(SourceStatus.NotReady);
        lease.Should().BeNull();
        player.Releases.Should().Be(1);
        source.RingOutsideFrames.Should().Be(1);
        source.TryDispose().Should().BeTrue();
    }

    [Fact]
    public void Dispose_WaitsForOutstandingLeases()
    {
        var player = new FakeLeasePlayer { HasRing = true, NextSlot = 0 };
        var factory = new FakeRingFactory();
        var source = new GStreamerSource(player, "gpu", device: null, onRingOpened: null, ringFactory: factory);
        source.TryAcquire(1, 0, out var lease).Should().Be(SourceStatus.Ready);
        source.TryDispose().Should().BeFalse();
        FluentActions.Invoking(() => source.Dispose()).Should().Throw<InvalidOperationException>();
        source.TryAcquire(1, 0, out var same).Should().Be(SourceStatus.Ready);
        lease!.Dispose();
        same!.Dispose();
        source.TryDispose().Should().BeTrue();
        source.Dispose();
        player.Releases.Should().Be(1);
    }

    [Fact]
    public void TryAcquire_ReopensRingWhenEpochChanges()
    {
        var player = new FakeLeasePlayer { HasRing = true, NextSlot = 0 };
        var factory = new FakeRingFactory();
        var source = new GStreamerSource(player, "gpu", device: null, onRingOpened: null, ringFactory: factory);
        source.TryAcquire(1, 0, out var first).Should().Be(SourceStatus.Ready);
        factory.Rings.Should().HaveCount(1);
        first!.Dispose();

        // shim が解像度変更でリングを作り直した（epoch 2, 1280x720）。
        player.NextRingEpoch = 2;
        player.RingInfo = new GstRingInfo(1280, 720, new IntPtr(9), [new IntPtr(1), new IntPtr(2), new IntPtr(3)], 2);
        source.TryAcquire(1, 0, out var second).Should().Be(SourceStatus.Ready);

        factory.Rings.Should().HaveCount(2);
        factory.Rings[1].Epoch.Should().Be(2);
        factory.Rings[1].Width.Should().Be(1280);
        factory.Rings[1].Height.Should().Be(720);
        factory.Rings[0].IsDisposed.Should().BeTrue("未返却リースが無い旧リングは開き直しで破棄する");
        second!.Dispose();
        source.TryDispose().Should().BeTrue();
    }

    [Fact]
    public void RingResourcesLifetime_KeepsOldRingUntilLeasesReturn()
    {
        var player = new FakeLeasePlayer();
        var source = new GStreamerSource(player, "gpu");
        var oldRing = new FakeRing(epoch: 1, width: 1920, height: 1080, count: 3);
        var shared = new GStreamerSource.SharedLease(source,
            new GstLeaseFrameInfo(1, 1, 0, 1920, 1080, true, 0, 1), IntPtr.Zero, 0, oldRing);
        oldRing.AddLeaseReference();   // TryAcquire が行う参照
        var held = shared.Retain();    // Held と合成 tick が共有するリース

        oldRing.ReleaseCurrentReference(); // 解像度変更で現行から降ろす
        oldRing.IsDisposed.Should().BeFalse("未返却リースが残っている間は旧リングを破棄しない");

        held.Dispose();
        oldRing.IsDisposed.Should().BeTrue("最後のリース返却で破棄する");
        player.Releases.Should().Be(1);
    }

    [Fact]
    public void TryAcquire_RingReopenFailure_ReturnsNotReady()
    {
        var player = new FakeLeasePlayer { HasRing = true, NextSlot = 0 };
        var factory = new FakeRingFactory { ThrowOnNextOpen = true };
        var source = new GStreamerSource(player, "gpu", device: null, onRingOpened: null, ringFactory: factory);
        source.TryAcquire(1, 0, out var lease).Should().Be(SourceStatus.NotReady);
        lease.Should().BeNull();
        player.Releases.Should().Be(1);
        source.TryDispose().Should().BeTrue();
    }
}
