using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer.Tests;

public sealed class OutputFrameTests
{
    [Fact]
    public void Normal_RetainsTheSamePixelsAndIdentityUntilItsOwnRelease()
    {
        var pool = new TrackingPool();
        using var snapshot = RenderedFrameSnapshot.Copy([73, 0, 0, 0], 1, 1, 4, 102, 2.5, pool);
        byte[] original = snapshot.Pixels;
        using var frame = OutputFrame.FromSnapshot(snapshot);
        Assert.Same(original, frame.PixelArray);
        Assert.Equal(1, pool.Rents);
        Assert.Equal((4, 102L, 2.5), (frame.Generation!.Value, frame.Sequence!.Value, frame.RenderMs!.Value));
        snapshot.Dispose();
        Assert.Throws<ObjectDisposedException>(() => snapshot.Retain());
        Assert.Throws<ObjectDisposedException>(() => OutputFrame.FromSnapshot(snapshot));
        Assert.Equal(73, frame.Pixels.Span[0]);
        Assert.Equal(0, pool.Returns);
        frame.Dispose();
        frame.Dispose();
        Assert.Equal(1, pool.Returns);
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    }

    [Fact]
    public void Snapshot_AChildCanRetainAfterOriginalDisposal_ButDisposedHandlesCannot()
    {
        var pool = new TrackingPool();
        using var root = RenderedFrameSnapshot.Copy([73, 0, 0, 0], 1, 1, 1, 1, 0, pool);
        using var child = root.Retain();
        root.Dispose();
        using var grandchild = child.Retain();
        child.Dispose();
        Assert.Throws<ObjectDisposedException>(() => child.Retain());
        Assert.Throws<ObjectDisposedException>(() => child.Pixels);
        Assert.Equal(73, grandchild.Pixels[0]);
        Assert.Equal(0, pool.Returns);
        grandchild.Dispose();
        Assert.Equal(1, pool.Returns);
    }

    [Fact]
    public async Task Snapshot_RetainRacingDisposeCannotAcquireReturnedPixelsOrReturnTwice()
    {
        var pool = new TrackingPool();
        using var root = RenderedFrameSnapshot.Copy([73, 0, 0, 0], 1, 1, 1, 1, 0, pool);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 16).Select(async _ =>
        {
            await start.Task;
            RenderedFrameSnapshot lease;
            try { lease = root.Retain(); }
            catch (ObjectDisposedException) { return; }
            using (lease)
            {
                Assert.Equal(73, lease.Pixels[0]);
                using var nested = lease.Retain();
                Assert.Equal(73, nested.Pixels[0]);
            }
        }).ToArray();
        var disposal = Task.Run(async () => { await start.Task; root.Dispose(); root.Dispose(); });
        start.SetResult();
        await Task.WhenAll(tasks.Append(disposal));
        Assert.Equal(1, pool.Returns);
    }

    [Theory]
    [InlineData("frozen")]
    [InlineData("gap")]
    [InlineData("buffered")]
    public void SelectedImage_OwnsPixelsAcrossSourceMutationResizeAndDisposal(string sourceKind)
    {
        using var buffers = new PixelBufferManager();
        buffers.EnsureFrozenFrameBuffer(2, 2);
        Array.Fill(buffers.FrozenFrameBuffer!, (byte)73);
        buffers.CopyFrozenToGapFreezeFrame(2, 2);
        var factory = new OutputFrameFactory(buffers);
        byte[] source = buffers.FrozenFrameBuffer!;
        using var frame = sourceKind switch
        {
            "frozen" => factory.Frozen(2, 2),
            "gap" => factory.GapFreeze(9, 9),
            _ => factory.Buffered(source, 2, 2)
        };
        Assert.NotNull(frame);
        Assert.NotSame(source, frame.PixelArray);
        Array.Fill(source, (byte)99);
        Array.Fill(buffers.CachedGapFreezeFrameBuffer!, (byte)11);
        buffers.EnsureFrozenFrameBuffer(8, 8);
        buffers.ClearGapFreezeFrame();
        buffers.Dispose();
        Assert.Equal((2, 2), (frame.Width, frame.Height));
        Assert.All(frame.Pixels.ToArray(), value => Assert.Equal(73, value));
        Assert.Null(frame.Generation);
        Assert.Null(frame.Sequence);
        Assert.Null(frame.RenderMs);
    }

    [Fact]
    public void Black_ClearsRentedPixelsWithoutChangingSourceBuffers()
    {
        var pool = new TrackingPool();
        using var black = OutputFrame.Copy(null, 2, 2, OutputFrameKind.Black, pool);
        Assert.All(black.Pixels.ToArray(), value => Assert.Equal(0, value));
        using var buffers = new PixelBufferManager();
        buffers.EnsurePixelBuffer(2, 2);
        Array.Fill(buffers.PixelBuffer!, (byte)73);
        using var fallback = new OutputFrameFactory(buffers).Black(0, 0);
        Assert.Equal((16, 16), (fallback.Width, fallback.Height));
        Assert.All(buffers.PixelBuffer!, value => Assert.Equal(73, value));
    }

    [Fact]
    public void IncompleteOrInvalidFramesAreRejectedBeforeRentingOrPublishing()
    {
        var pool = new TrackingPool();
        Assert.Throws<ArgumentException>(() => OutputFrame.Copy(new byte[3], 1, 1, OutputFrameKind.Buffered, pool));
        Assert.Throws<ArgumentNullException>(() => OutputFrame.Copy(null, 1, 1, OutputFrameKind.Buffered, pool));
        Assert.Throws<ArgumentOutOfRangeException>(() => OutputFrame.Copy(null, int.MaxValue, 2, OutputFrameKind.Black, pool));
        Assert.Equal(0, pool.Rents);
        using var buffers = new PixelBufferManager();
        var factory = new OutputFrameFactory(buffers);
        Assert.Null(factory.Buffered(new byte[3], 1, 1));
        Assert.Null(factory.Buffered(new byte[4], 0, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicationFailure_ReleasesTheOwnedImageAfterSendScopeExits(bool normal)
    {
        var pool = new TrackingPool();
        using var snapshot = normal ? RenderedFrameSnapshot.Copy([73, 0, 0, 0], 1, 1, 1, 1, 0, pool) : null;
        var failure = new InvalidOperationException("send failed");
        int cancels = 0;
        var pipeline = new RenderFramePublishPipeline(_ => 0,
            (pointer, _, _) =>
            {
                snapshot?.Dispose();
                Assert.Equal(0, pool.Returns);
                Assert.Equal(73, Marshal.ReadByte(pointer));
                throw failure;
            }, _ => Assert.Fail("failed publication has no performance sample"),
            (_, _, _, _) => throw new InvalidOperationException("No freeze copy after failure"),
            _ => Assert.Fail("No preview after failure"), () => cancels++);
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            using var frame = normal ? OutputFrame.FromSnapshot(snapshot!) : OutputFrame.Copy([73, 0, 0, 0], 1, 1, OutputFrameKind.Buffered, pool);
            pipeline.Publish(frame, true);
        });
        Assert.Same(failure, error);
        Assert.Equal(1, cancels);
        Assert.Equal(1, pool.Returns);
    }

    [Fact]
    public void NormalBitmapOnlyFlag_IsRejectedBeforeAnyOutput()
    {
        using var snapshot = RenderedFrameSnapshot.Copy(new byte[4], 1, 1, 1, 1, 0);
        using var frame = OutputFrame.FromSnapshot(snapshot);
        var pipeline = new RenderFramePublishPipeline(_ => throw new InvalidOperationException("No bitmap"),
            (_, _, _) => throw new InvalidOperationException("No send"), _ => { }, (_, _, _, _) => false);
        Assert.Throws<ArgumentException>(() => pipeline.Publish(frame, false, sendSpout: false));
    }

    [Theory]
    [InlineData("black")]
    [InlineData("frozen")]
    [InlineData("gap")]
    [InlineData("buffered")]
    public void BitmapObserverCannotChangeTheSelectedImageSentToEitherOutput(string sourceKind) => OnSta(() =>
    {
        using var buffers = new PixelBufferManager();
        buffers.EnsureFrozenFrameBuffer(2, 2);
        Array.Fill(buffers.FrozenFrameBuffer!, (byte)73);
        buffers.CopyFrozenToGapFreezeFrame(2, 2);
        byte[] source = buffers.FrozenFrameBuffer!;
        var factory = new OutputFrameFactory(buffers);
        using var frame = sourceKind switch
        {
            "black" => factory.Black(2, 2), "frozen" => factory.Frozen(2, 2),
            "gap" => factory.GapFreeze(9, 9), _ => factory.Buffered(source, 2, 2)
        };
        var renderer = new FrameRenderer();
        renderer.BitmapChanged += _ =>
        {
            Array.Fill(source, (byte)99);
            buffers.EnsureFrozenFrameBuffer(8, 8);
            buffers.ClearGapFreezeFrame();
            buffers.Dispose();
        };
        var calls = new List<string>();
        var pipeline = new RenderFramePublishPipeline(image => { renderer.Update(image); calls.Add("bitmap"); return 0; },
            (pointer, w, h) =>
            {
                var sent = new byte[w * h * 4];
                Marshal.Copy(pointer, sent, 0, sent.Length);
                var bitmapPixels = new byte[sent.Length];
                renderer.CurrentBitmap!.CopyPixels(bitmapPixels, w * 4, 0);
                Assert.Equal(frame!.Pixels.ToArray(), sent);
                Assert.Equal(bitmapPixels, sent);
                Assert.All(sent, value => Assert.Equal(sourceKind == "black" ? 0 : 73, value));
                calls.Add("send");
                return 0;
            }, _ => Assert.Fail("Special image must not record native performance"),
            (_, _, _, _) => throw new InvalidOperationException("Special image must not replace freeze source"),
            _ => calls.Add("preview"),
            updateDisplayWithSpout: (_, _) => throw new InvalidOperationException("Special image must not use combined ordering"));
        pipeline.Publish(frame!, true, combineBitmapAndSpout: true);
        Assert.Equal(new[] { "bitmap", "send", "preview" }, calls);
    });

    private sealed class TrackingPool : ArrayPool<byte>
    {
        public int Rents, Returns;
        public override byte[] Rent(int minimumLength)
        {
            Interlocked.Increment(ref Rents);
            return Enumerable.Repeat((byte)255, minimumLength).ToArray();
        }
        public override void Return(byte[] array, bool clearArray = false)
        {
            Array.Fill(array, (byte)255);
            Interlocked.Increment(ref Returns);
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
