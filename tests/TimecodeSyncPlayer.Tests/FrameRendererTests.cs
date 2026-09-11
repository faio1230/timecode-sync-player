using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
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
        public List<(byte[] Pixels, int Width, int Height)> SentFrames { get; } = [];
        public bool TryInitialize() => true;
        public void SendFrame(IntPtr pixels, int width, int height)
        {
            var copy = new byte[width * height * 4];
            Marshal.Copy(pixels, copy, 0, copy.Length);
            SentFrames.Add((copy, width, height));
        }
        public void Dispose() { }
    }

    [Fact]
    public void UpdateSourceBitmap_CopiesPixelsAndOnlyRaisesChangedForNewSize()
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            buffers.EnsurePixelBuffer(3, 2);
            FillWithPattern(buffers.PixelBuffer!, 17);
            var renderer = new OutputFrameTestHarness(buffers, new FakeSpout());
            var bitmaps = new List<WriteableBitmap>();
            renderer.BitmapChanged += bitmaps.Add;

            renderer.UpdateSourceBitmap(2, 2);
            renderer.UpdateSourceBitmap(2, 2);
            renderer.UpdateSourceBitmap(3, 2);

            bitmaps.Should().HaveCount(2);
            bitmaps[0].PixelWidth.Should().Be(2);
            bitmaps[0].PixelHeight.Should().Be(2);
            ReadPixels(bitmaps[0]).Should().Equal(buffers.PixelBuffer!.Take(16));
            ReadPixels(bitmaps[1]).Should().Equal(buffers.PixelBuffer);
        });
    }

    [Fact]
    public void BlackSelection_CreatesBitmapAndSendsResolvedBlackPixels()
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            buffers.EnsurePixelBuffer(2, 2);
            FillWithPattern(buffers.PixelBuffer!, 1);
            var spout = new FakeSpout();
            var renderer = new OutputFrameTestHarness(buffers, spout);
            WriteableBitmap? bitmap = null;
            renderer.BitmapChanged += value => bitmap = value;

            renderer.PublishBlack(2, 2);

            bitmap.Should().NotBeNull();
            ReadPixels(bitmap!).Should().OnlyContain(value => value == 0);
            AssertSent(spout, new byte[2 * 2 * 4], 2, 2);
        });
    }

    [Fact]
    public void FrozenSelection_PublishesTheSamePixelsToBitmapAndSpout()
    {
        RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            buffers.EnsureFrozenFrameBuffer(2, 2);
            FillWithPattern(buffers.FrozenFrameBuffer!, 31);
            var spout = new FakeSpout();
            var renderer = new OutputFrameTestHarness(buffers, spout);
            WriteableBitmap? bitmap = null;
            renderer.BitmapChanged += value => bitmap = value;

            renderer.PublishFrozen(2, 2);

            ReadPixels(bitmap!).Should().Equal(buffers.FrozenFrameBuffer);
            AssertSent(spout, buffers.FrozenFrameBuffer!, 2, 2);
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
            var renderer = new OutputFrameTestHarness(buffers, spout);
            WriteableBitmap? bitmap = null;
            renderer.BitmapChanged += value => bitmap = value;

            renderer.PublishFrozen(width, height);

            int expectedWidth = width > 0 ? width : 16;
            int expectedHeight = height > 0 ? height : 16;
            ReadPixels(bitmap!).Should().OnlyContain(value => value == 0);
            AssertSent(spout, new byte[expectedWidth * expectedHeight * 4], expectedWidth, expectedHeight);
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
            var renderer = new OutputFrameTestHarness(buffers, spout);
            WriteableBitmap? bitmap = null;
            renderer.BitmapChanged += value => bitmap = value;

            renderer.PublishGapFreeze(9, 9);

            bitmap!.PixelWidth.Should().Be(3);
            bitmap.PixelHeight.Should().Be(2);
            ReadPixels(bitmap).Should().Equal(buffers.CachedGapFreezeFrameBuffer);
            AssertSent(spout, buffers.CachedGapFreezeFrameBuffer!, 3, 2);
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
            var renderer = new OutputFrameTestHarness(buffers, spout);
            WriteableBitmap? bitmap = null;
            renderer.BitmapChanged += value => bitmap = value;

            renderer.PublishGapFreeze(2, 2);

            ReadPixels(bitmap!).Should().Equal(buffers.FrozenFrameBuffer);
            AssertSent(spout, buffers.FrozenFrameBuffer!, 2, 2);
        });
    }

    [Fact]
    public void BufferedSelection_PublishesTheSamePixelsToBitmapAndSpout()
    {
        RunOnSta(() =>
        {
            byte[] buffer = new byte[16];
            FillWithPattern(buffer, 79);
            var spout = new FakeSpout();
            using var buffers = new PixelBufferManager();
            var renderer = new OutputFrameTestHarness(buffers, spout);
            WriteableBitmap? bitmap = null;
            renderer.BitmapChanged += value => bitmap = value;

            renderer.PublishBuffered(buffer, 2, 2);

            ReadPixels(bitmap!).Should().Equal(buffer);
            AssertSent(spout, buffer, 2, 2);
        });
    }

    [Fact]
    public void RenderBuffered_ShortBufferReturnsWithoutBitmapOrSpoutCall()
    {
        RunOnSta(() =>
        {
            var spout = new FakeSpout();
            using var buffers = new PixelBufferManager();
            var renderer = new OutputFrameTestHarness(buffers, spout);
            int changedCount = 0;
            renderer.BitmapChanged += _ => changedCount++;

            renderer.PublishBuffered(new byte[15], 2, 2);

            changedCount.Should().Be(0);
            spout.SentFrames.Should().BeEmpty();
        });
    }

    [Fact]
    public void BufferedSelection_ExplicitBitmapOnlyFlagSkipsSpout()
    {
        RunOnSta(() =>
        {
            var spout = new FakeSpout();
            using var buffers = new PixelBufferManager();
            var renderer = new OutputFrameTestHarness(buffers, spout);
            WriteableBitmap? bitmap = null;
            renderer.BitmapChanged += value => bitmap = value;

            renderer.PublishBuffered(new byte[16], 2, 2, sendSpout: false);

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
    public void CopyEntryPoints_InvalidDimensionsReturnSafely(
        string operation,
        int width,
        int height)
    {
        Action act = () => RunOnSta(() =>
        {
            using var buffers = new PixelBufferManager();
            buffers.EnsurePixelBuffer(1, 1);
            buffers.EnsureFrozenFrameBuffer(1, 1);
            var renderer = new OutputFrameTestHarness(buffers, new FakeSpout());

            switch (operation)
            {
                case "update":
                    renderer.UpdateSourceBitmap(width, height);
                    break;
                case "buffered":
                    renderer.PublishBuffered(new byte[4], width, height, sendSpout: false);
                    break;
                case "frozen":
                    renderer.PublishFrozen(width, height);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown operation: {operation}");
            }
        });

        act.Should().NotThrow();
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
            var renderer = new OutputFrameTestHarness(buffers, new FakeSpout());
            int changedCount = 0;
            renderer.BitmapChanged += _ => changedCount++;

            Action act = () => renderer.PublishBuffered(Array.Empty<byte>(), width, height, sendSpout: false);

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
            var renderer = new OutputFrameTestHarness(buffers, spout);

            renderer.PublishBlack(width, height);

            AssertSent(spout, new byte[16 * 16 * 4], 16, 16);
        });
    }

    private static void AssertSent(FakeSpout spout, byte[] expected, int width, int height)
    {
        var sent = spout.SentFrames.Should().ContainSingle().Which;
        sent.Width.Should().Be(width);
        sent.Height.Should().Be(height);
        sent.Pixels.Should().Equal(expected.Take(width * height * 4));
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
