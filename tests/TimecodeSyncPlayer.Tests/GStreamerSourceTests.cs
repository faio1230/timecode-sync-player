using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class GStreamerSourceTests
{
    private sealed class FakeLeasePlayer : IGstLeasePlayer
    {
        public ulong Gen = 1;
        public bool HasFrame = true;
        public ulong NextSequence = 1;
        public int NextSlot = -1;
        public bool HasRing;
        public GstRingInfo RingInfo = new(1920, 1080, new IntPtr(9), [new IntPtr(1), new IntPtr(2), new IntPtr(3)]);
        public int Releases;
        public int Acquires;

        public ulong Generation => Gen;
        public void SetGeneration(ulong generation) { Gen = generation; HasFrame = false; }
        public bool Ended;
        public int Acquire(ulong generation, out GstLeaseFrameInfo info)
        {
            if (Ended) { info = default; return -6; }
            if (!HasFrame || generation != Gen) { info = default; return 0; }
            info = new GstLeaseFrameInfo(Gen, NextSequence, (long)((NextSequence - 1) * 0.04 * 1_000_000_000), 1920, 1080, true, NextSlot);
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
}
