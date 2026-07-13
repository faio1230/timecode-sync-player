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
