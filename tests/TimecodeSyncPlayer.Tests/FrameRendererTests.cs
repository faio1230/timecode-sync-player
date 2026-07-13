using Xunit;

namespace TimecodeSyncPlayer.Tests;

public class FrameRendererTests
{
    private sealed class FakeSpout : ISpoutOutput
    {
        public bool IsEnabled { get; set; } = true;
        public bool IsAvailable => true;
        public bool TryInitialize() => true;
        public void SendFrame(IntPtr pixels, int width, int height) { }
        public void Dispose() { }
    }

    [Fact]
    public void Constructor_CreatesInstance()
    {
        // WriteableBitmapはWPFのUIスレッドが必要なためレンダリング動作のテストは行わない。
        // コンストラクタの依存性解決確認のみ実施する。
        var bufferManager = new PixelBufferManager();
        var spout = new FakeSpout();
        var renderer = new FrameRenderer(bufferManager, spout);
        Assert.NotNull(renderer);
    }
}
