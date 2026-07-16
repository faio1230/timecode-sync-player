using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class FrameRendererTests
{
    private sealed class FakeSpout : ISpoutOutput
    {
        public bool IsEnabled { get; set; } = true;
        public bool IsAvailable => true;
        public List<(IntPtr Pixels, int Width, int Height)> SentFrames { get; } = [];
        public bool TryInitialize() => true;
        public void SendFrame(IntPtr pixels, int width, int height) =>
            SentFrames.Add((pixels, width, height));
        public void Dispose() { }
    }

    [Fact]
    public void UpdateFromPixelBuffer_WithoutBuffer_DoesNotCreateBitmap()
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            var renderer = new FrameRenderer(buffers, new FakeSpout());
            int changedCount = 0;
            renderer.BitmapChanged += _ => changedCount++;

            renderer.UpdateFromPixelBuffer(2, 2);

            changedCount.Should().Be(0);
        });
    }

    [Fact]
    public void UpdateFromPixelBuffer_CopiesPixelsAndOnlyRaisesChangedForNewSize()
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            buffers.EnsurePixelBuffer(3, 2);
            FillWithPattern(buffers.PixelBuffer!, 17);
            var renderer = new FrameRenderer(buffers, new FakeSpout());
            var bitmaps = new List<WriteableBitmap>();
            renderer.BitmapChanged += bitmaps.Add;

            renderer.UpdateFromPixelBuffer(2, 2);
            renderer.UpdateFromPixelBuffer(2, 2);
            renderer.UpdateFromPixelBuffer(3, 2);

            bitmaps.Should().HaveCount(2);
            bitmaps[0].PixelWidth.Should().Be(2);
            bitmaps[0].PixelHeight.Should().Be(2);
            ReadPixels(bitmaps[0]).Should().Equal(buffers.PixelBuffer!.Take(16));
            ReadPixels(bitmaps[1]).Should().Equal(buffers.PixelBuffer);
        });
    }

    [Fact]
    public void RenderBlack_ClearsPixelsCreatesBitmapAndSendsResolvedSize()
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            buffers.EnsurePixelBuffer(2, 2);
            FillWithPattern(buffers.PixelBuffer!, 1);
            var spout = new FakeSpout();
            var renderer = new FrameRenderer(buffers, spout);
            WriteableBitmap? bitmap = null;
            renderer.BitmapChanged += value => bitmap = value;

            renderer.RenderBlack(2, 2);

            bitmap.Should().NotBeNull();
            ReadPixels(bitmap!).Should().OnlyContain(value => value == 0);
            spout.SentFrames.Should().ContainSingle().Which.Should().Be((buffers.PixelPtr, 2, 2));
        });
    }

    [Fact]
    public void RenderFrozen_ValidFrameCopiesPixelsAndSendsFrozenPointer()
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            buffers.EnsureFrozenFrameBuffer(2, 2);
            FillWithPattern(buffers.FrozenFrameBuffer!, 31);
            var spout = new FakeSpout();
            var renderer = new FrameRenderer(buffers, spout);
            WriteableBitmap? bitmap = null;
            renderer.BitmapChanged += value => bitmap = value;

            renderer.RenderFrozen(2, 2);

            ReadPixels(bitmap!).Should().Equal(buffers.FrozenFrameBuffer);
            spout.SentFrames.Should().ContainSingle().Which.Should().Be((buffers.FrozenFramePtr, 2, 2));
        });
    }

    [Theory]
    [InlineData(false, 2, 2)]
    [InlineData(true, 0, 2)]
    [InlineData(true, 2, 0)]
    [InlineData(true, 2, 2)]
    public void RenderFrozen_UnavailableOrTooSmallFallsBackToBlack(
        bool allocateSmallFrozenFrame,
        int width,
        int height)
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            if (allocateSmallFrozenFrame)
            {
                buffers.EnsureFrozenFrameBuffer(1, 1);
            }
            var spout = new FakeSpout();
            var renderer = new FrameRenderer(buffers, spout);
            WriteableBitmap? bitmap = null;
            renderer.BitmapChanged += value => bitmap = value;

            renderer.RenderFrozen(width, height);

            int expectedWidth = width > 0 ? width : 16;
            int expectedHeight = height > 0 ? height : 16;
            ReadPixels(bitmap!).Should().OnlyContain(value => value == 0);
            spout.SentFrames.Should().ContainSingle().Which
                .Should().Be((buffers.PixelPtr, expectedWidth, expectedHeight));
        });
    }

    [Fact]
    public void RenderGapFreeze_WithCachedFrameUsesCachedDimensionsAndPixels()
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            buffers.EnsureFrozenFrameBuffer(3, 2);
            FillWithPattern(buffers.FrozenFrameBuffer!, 47);
            buffers.CopyFrozenToGapFreezeFrame(3, 2);
            var spout = new FakeSpout();
            var renderer = new FrameRenderer(buffers, spout);
            WriteableBitmap? bitmap = null;
            renderer.BitmapChanged += value => bitmap = value;

            renderer.RenderGapFreeze(9, 9);

            bitmap!.PixelWidth.Should().Be(3);
            bitmap.PixelHeight.Should().Be(2);
            ReadPixels(bitmap).Should().Equal(buffers.CachedGapFreezeFrameBuffer);
            spout.SentFrames.Should().ContainSingle().Which
                .Should().Be((buffers.CachedGapFreezeFramePtr, 3, 2));
        });
    }

    [Fact]
    public void RenderGapFreeze_WithoutCachedFrameUsesFrozenFrame()
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            buffers.EnsureFrozenFrameBuffer(2, 2);
            FillWithPattern(buffers.FrozenFrameBuffer!, 63);
            var spout = new FakeSpout();
            var renderer = new FrameRenderer(buffers, spout);
            WriteableBitmap? bitmap = null;
            renderer.BitmapChanged += value => bitmap = value;

            renderer.RenderGapFreeze(2, 2);

            ReadPixels(bitmap!).Should().Equal(buffers.FrozenFrameBuffer);
            spout.SentFrames.Should().ContainSingle().Which.Should().Be((buffers.FrozenFramePtr, 2, 2));
        });
    }

    [Fact]
    public void RenderBuffered_ValidBufferCopiesPixelsAndForwardsNonZeroHandle()
    {
        RunOnSta(() =>
        {
            byte[] buffer = new byte[16];
            FillWithPattern(buffer, 79);
            var spout = new FakeSpout();
            using var buffers = new PixelBufferManager();
            var renderer = new FrameRenderer(buffers, spout);
            WriteableBitmap? bitmap = null;
            renderer.BitmapChanged += value => bitmap = value;
            var handle = new IntPtr(1234);

            renderer.RenderBuffered(buffer, handle, 2, 2);

            ReadPixels(bitmap!).Should().Equal(buffer);
            spout.SentFrames.Should().ContainSingle().Which.Should().Be((handle, 2, 2));
        });
    }

    [Fact]
    public void RenderBuffered_ShortBufferReturnsWithoutBitmapOrSpoutCall()
    {
        RunOnSta(() =>
        {
            var spout = new FakeSpout();
            using var buffers = new PixelBufferManager();
            var renderer = new FrameRenderer(buffers, spout);
            int changedCount = 0;
            renderer.BitmapChanged += _ => changedCount++;

            renderer.RenderBuffered(new byte[15], new IntPtr(1234), 2, 2);

            changedCount.Should().Be(0);
            spout.SentFrames.Should().BeEmpty();
        });
    }

    [Fact]
    public void RenderBuffered_ZeroHandleRendersWithoutSpoutCall()
    {
        RunOnSta(() =>
        {
            var spout = new FakeSpout();
            using var buffers = new PixelBufferManager();
            var renderer = new FrameRenderer(buffers, spout);
            WriteableBitmap? bitmap = null;
            renderer.BitmapChanged += value => bitmap = value;

            renderer.RenderBuffered(new byte[16], IntPtr.Zero, 2, 2);

            bitmap.Should().NotBeNull();
            spout.SentFrames.Should().BeEmpty();
        });
    }

    [Theory]
    [InlineData("update", 0, 1)]
    [InlineData("update", -1, 1)]
    [InlineData("update", 32_768, 32_768)]
    [InlineData("update", 65_536, 65_536)]
    [InlineData("buffered", 0, 1)]
    [InlineData("buffered", -1, 1)]
    [InlineData("buffered", 32_768, 32_768)]
    [InlineData("buffered", 65_536, 65_536)]
    [InlineData("frozen", 32_768, 32_768)]
    [InlineData("frozen", 65_536, 65_536)]
    public void CopyEntryPoints_InvalidDimensionsThrowArgumentOutOfRange(
        string operation,
        int width,
        int height)
    {
        Action act = () => RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            buffers.EnsurePixelBuffer(1, 1);
            buffers.EnsureFrozenFrameBuffer(1, 1);
            var renderer = new FrameRenderer(buffers, new FakeSpout());

            switch (operation)
            {
                case "update":
                    renderer.UpdateFromPixelBuffer(width, height);
                    break;
                case "buffered":
                    renderer.RenderBuffered(new byte[4], IntPtr.Zero, width, height);
                    break;
                case "frozen":
                    renderer.RenderFrozen(width, height);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown operation: {operation}");
            }
        });

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithMessage("*frame dimensions*");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(32_768, 1)]
    public void RenderBuffered_ValidBoundaryDimensionsWithShortBufferReturnSafely(
        int width,
        int height)
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            var renderer = new FrameRenderer(buffers, new FakeSpout());
            int changedCount = 0;
            renderer.BitmapChanged += _ => changedCount++;

            Action act = () => renderer.RenderBuffered(Array.Empty<byte>(), IntPtr.Zero, width, height);

            act.Should().NotThrow();
            changedCount.Should().Be(0);
        });
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    public void RenderBlack_NonPositiveVideoSizeUsesSafeFallback(int width, int height)
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            var spout = new FakeSpout();
            var renderer = new FrameRenderer(buffers, spout);

            renderer.RenderBlack(width, height);

            spout.SentFrames.Should().ContainSingle().Which
                .Should().Be((buffers.PixelPtr, 16, 16));
        });
    }

    private static byte[] ReadPixels(WriteableBitmap bitmap)
    {
        int stride = bitmap.PixelWidth * 4;
        byte[] pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static void FillWithPattern(byte[] buffer, byte start)
    {
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(start + i);
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
