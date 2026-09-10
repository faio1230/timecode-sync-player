using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class SpoutFramePublisherTests
{
    [Fact]
    public void Publish_SendsFrameAndReturnsElapsedMilliseconds()
    {
        var spout = new FakeSpoutOutput();
        var publisher = new SpoutFramePublisher(spout);
        IntPtr pixels = new(123);

        double elapsedMs = publisher.Publish(pixels, 320, 180);

        elapsedMs.Should().BeGreaterThanOrEqualTo(0);
        spout.SentFrames.Should().Equal((pixels, 320, 180));
    }

    [Fact]
    public void Publish_PrefersGpuFrameWhenPublisherSupportsIt()
    {
        var spout = new GpuFakeSpoutOutput { GpuResult = true };
        var publisher = new SpoutFramePublisher(spout);

        publisher.Publish(new IntPtr(123), 320, 180);

        spout.GpuAttempts.Should().Be(1);
        spout.SentFrames.Should().BeEmpty();
    }

    [Fact]
    public void Publish_FallsBackToCpuPixelsWhenGpuPublishFails()
    {
        var spout = new GpuFakeSpoutOutput { GpuResult = false };
        var publisher = new SpoutFramePublisher(spout);
        IntPtr pixels = new(7);

        publisher.Publish(pixels, 64, 64);

        spout.GpuAttempts.Should().Be(1);
        spout.SentFrames.Should().Equal((pixels, 64, 64));
    }

    private sealed class GpuFakeSpoutOutput : ISpoutOutput, IGpuSpoutPublisher
    {
        public List<(IntPtr Pixels, int Width, int Height)> SentFrames { get; } = [];
        public bool GpuResult { get; set; }
        public int GpuAttempts { get; private set; }
        public bool IsEnabled { get; set; } = true;
        public bool IsAvailable => true;
        public bool TryInitialize() => true;
        public void SendFrame(IntPtr pixels, int width, int height) => SentFrames.Add((pixels, width, height));
        public bool TryPublishCurrentGpuFrame()
        {
            GpuAttempts++;
            return GpuResult;
        }
        public void Dispose() { }
    }

    private sealed class FakeSpoutOutput : ISpoutOutput
    {
        public List<(IntPtr Pixels, int Width, int Height)> SentFrames { get; } = [];
        public bool IsEnabled { get; set; } = true;
        public bool IsAvailable => true;
        public bool TryInitialize() => true;
        public void SendFrame(IntPtr pixels, int width, int height) => SentFrames.Add((pixels, width, height));
        public void Dispose() { }
    }
}
