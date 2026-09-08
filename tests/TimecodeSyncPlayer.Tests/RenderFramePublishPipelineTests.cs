using FluentAssertions;
using System.Buffers;
using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer.Tests;

public class RenderFramePublishPipelineTests
{
    [Fact]
    public void Publish_RunsDisplaySpoutPerformanceAndFreezeCopyInOrder()
    {
        var calls = new List<string>();
        byte[] source = [123, 0, 0, 0];
        var pipeline = new RenderFramePublishPipeline(
            updateDisplay: (pixels, width, height) =>
            {
                pixels.Should().BeSameAs(source);
                calls.Add($"display:{width}x{height}");
                return 2.0;
            },
            publishSpout: (pixels, width, height) =>
            {
                calls.Add($"spout:{width}x{height}:{Marshal.ReadByte(pixels)}");
                return 3.0;
            },
            recordPerformance: measurement => calls.Add($"perf:{measurement.RenderMs}:{measurement.BitmapMs}:{measurement.SpoutMs}:{measurement.SpoutEnabled}"),
            copyFreezeFrame: (pixels, state, width, height) =>
            {
                pixels.Should().BeSameAs(source);
                calls.Add($"freeze:{state}:{width}x{height}");
                return true;
            });

        pipeline.Publish(
            pixels: source,
            width: 320,
            height: 180,
            renderMs: 1.0,
            spoutEnabled: true,
            gapState: GapState.WaitingForFrameStep);

        calls.Should().Equal(
            "display:320x180",
            "spout:320x180:123",
            "perf:1:2:3:True",
            "freeze:WaitingForFrameStep:320x180");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Publish_KeepsLeasedPixelsStableDuringMailboxReplacementAndSpoutFailure(bool failSpout)
    {
        var pool = new PoisonOnReturnPool();
        using var mailbox = new LatestRenderedFrameMailbox();
        mailbox.Publish(RenderedFrameSnapshot.Copy([73, 0, 0, 0], 1, 1, 1, 1, 0, pool));
        using var lease = mailbox.Take()!;
        byte[] pixels = lease.Pixels;
        var failure = new InvalidOperationException("Spout failure");
        using var buffers = new PixelBufferManager();
        var copier = new RenderedFrameFreezeBufferCopier(buffers);
        var pipeline = new RenderFramePublishPipeline(
            (source, _, _) => { source.Should().BeSameAs(pixels); return 0; },
            (pointer, _, _) =>
            {
                for (int i = 0; i < 10; i++)
                    mailbox.Publish(RenderedFrameSnapshot.Copy([99, 0, 0, 0], 1, 1, 1, i + 2, 0, pool));
                mailbox.Dispose(); // Shutdown may return pending frames, never this UI lease.
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                Marshal.ReadByte(pointer).Should().Be(73);
                pixels[0].Should().Be(73);
                pool.Outstanding.Should().Be(1);
                if (failSpout) throw failure;
                return 0;
            },
            _ => { },
            (source, state, w, h) => copier.CopyIfNeeded(source, state, w, h));
        Action publish = () => pipeline.Publish(pixels, 1, 1, 0, true, GapState.WaitingForFrameStep);
        if (failSpout) publish.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(failure);
        else publish();
        pool.Outstanding.Should().Be(1);
        lease.Dispose();
        pool.Outstanding.Should().Be(0);
        pixels[0].Should().Be(255);
        if (failSpout) buffers.FrozenFrameBuffer.Should().BeNull();
        else buffers.FrozenFrameBuffer![0].Should().Be(73, "freeze owns a copy after the snapshot is returned");
    }

    private sealed class PoisonOnReturnPool : ArrayPool<byte>
    {
        public int Outstanding;
        public override byte[] Rent(int minimumLength) { Outstanding++; return new byte[minimumLength]; }
        public override void Return(byte[] array, bool clearArray = false) { Array.Fill(array, (byte)255); Outstanding--; }
    }
}
