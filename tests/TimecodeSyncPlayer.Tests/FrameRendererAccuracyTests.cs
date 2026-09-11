using System.IO;
using System.Runtime.ExceptionServices;

namespace TimecodeSyncPlayer.Tests;

public class FrameRendererAccuracyTests
{
    [Fact]
    public void EveryPublicationPath_RecordsOneEventWhoseValidityComesFromPixels()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jsonl");
        try
        {
            RunOnSta(() =>
            {
                using var trace = SyncAccuracyTrace.Create(path);
                using var buffers = new PixelBufferManager();
                buffers.EnsurePixelBuffer(1920, 1080);
                AccuracyFrameMarkerTests.GoldenPixels().CopyTo(buffers.PixelBuffer!, 0);
                var renderer = new OutputFrameTestHarness(buffers, new NullSpout(), trace);
                renderer.UpdateSourceBitmap(1920, 1080);
                buffers.EnsureFrozenFrameBuffer(1920, 1080);
                buffers.CopyToFrozenFrame(1920, 1080);
                renderer.PublishFrozen(1920, 1080);
                buffers.CopyFrozenToGapFreezeFrame(1920, 1080);
                renderer.PublishGapFreeze(1920, 1080);
                renderer.PublishBlack(1920, 1080);
                // Black selection does not modify source storage. Supply black for the next normal image.
                buffers.ClearPixelBuffer();
                renderer.UpdateSourceBitmap(1920, 1080);
                renderer.PublishBuffered(new byte[1], 1920, 1080, sendSpout: false);
            });
            var frames = SyncAccuracyTraceTests.Read(path).Where(x => x.GetProperty("type").GetString() == "frame").ToArray();
            Assert.Equal(new[] { "normal", "frozen", "buffered", "black", "normal" }, frames.Select(x => x.GetProperty("kind").GetString()));
            foreach (var frame in frames.Take(3))
            {
                Assert.True(frame.GetProperty("markerValid").GetBoolean());
                Assert.Equal(0x1234, frame.GetProperty("frameIndex").GetInt32());
            }
            foreach (var frame in frames.Skip(3))
            {
                Assert.True(frame.GetProperty("isBlack").GetBoolean());
                Assert.False(frame.GetProperty("markerValid").GetBoolean());
            }
        }
        finally { File.Delete(path); }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class NullSpout : ISpoutOutput
    {
        public bool IsEnabled { get; set; }
        public bool IsAvailable => false;
        public bool TryInitialize() => false;
        public void SendFrame(IntPtr pixels, int width, int height) { }
        public void Dispose() { }
    }
}
