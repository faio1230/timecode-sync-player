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
        public int Releases;
        public int Acquires;

        public ulong Generation => Gen;
        public void SetGeneration(ulong generation) { Gen = generation; HasFrame = false; }
        public bool Acquire(ulong generation, out GstLeaseFrameInfo info)
        {
            if (!HasFrame || generation != Gen) { info = default; return false; }
            info = new GstLeaseFrameInfo(Gen, NextSequence, (long)((NextSequence - 1) * 0.04 * 1_000_000_000), 1920, 1080, true);
            Acquires++;
            return true;
        }
        public bool TryGetLeasedTexture(out IntPtr texture, out uint subresource, out uint dxgiFormat)
        {
            texture = IntPtr.Zero;
            subresource = 0;
            dxgiFormat = 87;
            return true;
        }
        public void Release() { Releases++; NextSequence++; }
        public string DecoderName => "d3d11h264dec";
        public GstDeliveryStatsInfo DeliveryStats { get; set; }
    }

    [Fact]
    public void TryAcquire_ReturnsTheCurrentGenerationFrameAndSharesTheLeaseWhileHeld()
    {
        var player = new FakeLeasePlayer();
        var source = new GStreamerSource(player, "gpu");
        source.TryAcquire(1, 0, out var first).Should().Be(SourceStatus.Ready);
        first!.Stamp.Generation.Should().Be(1);
        first.Stamp.Sequence.Should().Be(1);
        first.Width.Should().Be(1920);
        first.Height.Should().Be(1080);
        first.Texture.Should().BeNull();
        first.Format.Should().Be(SourceImageFormat.Bgra8);

        // リース保持中の再取得は同じリース（同じ Stamp）を返す。shim のセマンティクスを吸収する。
        source.TryAcquire(1, 0, out var second).Should().Be(SourceStatus.Ready);
        second!.Stamp.Should().Be(first.Stamp);
        player.Releases.Should().Be(0);

        first.Dispose();
        player.Releases.Should().Be(0);
        second.Dispose();
        player.Releases.Should().Be(1);
        source.TryDispose().Should().BeTrue();
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
    public void Dispose_WaitsForOutstandingLeases()
    {
        var player = new FakeLeasePlayer();
        var source = new GStreamerSource(player);
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
    public void SetGeneration_MakesOlderImagesUnavailableAndKeepsTheShimLeaseAlive()
    {
        var player = new FakeLeasePlayer();
        var source = new GStreamerSource(player);
        source.TryAcquire(1, 0, out var lease).Should().Be(SourceStatus.Ready);
        source.SetGeneration(2);
        source.TryAcquire(1, 0, out _).Should().Be(SourceStatus.NotReady);
        source.TryAcquire(2, 0, out _).Should().Be(SourceStatus.NotReady);
        lease!.Dispose();
        player.Releases.Should().Be(1);
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
        source.TryAcquire(1, 0, out var lease).Should().Be(SourceStatus.Ready);
        FluentActions.Invoking(() => lease!.CompleteGpuUse()).Should().Throw<InvalidOperationException>();
        lease!.BeginGpuUse();
        FluentActions.Invoking(() => lease.BeginGpuUse()).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => lease!.Dispose()).Should().Throw<InvalidOperationException>();
        lease.CompleteGpuUse();
        lease.Dispose();
        lease.Dispose();
        player.Releases.Should().Be(1);
        source.TryDispose().Should().BeTrue();
    }
}
