using System.Windows.Media.Imaging;

namespace TimecodeSyncPlayer.Tests;

internal sealed class OutputFrameTestHarness
{
    private readonly PixelBufferManager _buffers;
    private readonly OutputFrameFactory _factory;
    private readonly RenderFramePublishPipeline _pipeline;
    private readonly ISpoutOutput _spout;
    public FrameRenderer Renderer { get; }

    public OutputFrameTestHarness(PixelBufferManager buffers, ISpoutOutput spout,
        SyncAccuracyTrace? trace = null, Action<WriteableBitmap, string>? queuePreview = null,
        Action? cancelPreview = null)
    {
        _buffers = buffers;
        _factory = new OutputFrameFactory(buffers);
        _spout = spout;
        Renderer = new FrameRenderer(trace, queuePreview, cancelPreview);
        _pipeline = new RenderFramePublishPipeline(frame => { Renderer.Update(frame); return 0; },
            (pointer, w, h) => { spout.SendFrame(pointer, w, h); return 0; },
            _ => { }, (_, _, _, _) => false, Renderer.QueuePreviewFromCurrentBitmap, cancelPreview);
    }
    public event Action<WriteableBitmap> BitmapChanged
    {
        add => Renderer.BitmapChanged += value;
        remove => Renderer.BitmapChanged -= value;
    }
    public WriteableBitmap? CurrentBitmap => Renderer.CurrentBitmap;
    public void UpdateSourceBitmap(int width, int height)
    {
        if (_buffers.PixelBuffer is { } pixels) Renderer.UpdateFromPixels(pixels, width, height);
    }
    public void UpdateFromPixels(byte[] pixels, int width, int height) => Renderer.UpdateFromPixels(pixels, width, height);
    public void QueuePreviewFromCurrentBitmap(string kind) => Renderer.QueuePreviewFromCurrentBitmap(kind);
    public void PublishBlack(int width, int height) => Publish(_factory.Black(width, height));
    public void PublishFrozen(int width, int height) => Publish(_factory.Frozen(width, height));
    public void PublishGapFreeze(int width, int height) => Publish(_factory.GapFreeze(width, height));
    public void PublishBuffered(byte[] pixels, int width, int height, bool sendSpout = true)
        => Publish(_factory.Buffered(pixels, width, height), sendSpout);
    private void Publish(OutputFrame? frame, bool sendSpout = true)
    {
        using (frame)
            if (frame != null) _pipeline.Publish(frame, _spout.IsEnabled, sendSpout: sendSpout);
    }
}

internal static class NormalFrameTestPublication
{
    // Test input becomes a complete owned native snapshot before crossing the product boundary.
    public static void PublishNormal(this RenderFramePublishPipeline pipeline, byte[] pixels, int width, int height,
        double renderMs, bool spoutEnabled, GapState gapState, SyncAccuracyTrace? trace = null,
        long sessionId = 0, int generation = 0, long sequence = 0, bool combineBitmapAndSpout = false)
    {
        using var snapshot = RenderedFrameSnapshot.Copy(pixels, width, height, generation, sequence, renderMs);
        using var frame = OutputFrame.FromSnapshot(snapshot);
        pipeline.Publish(frame, spoutEnabled, gapState, trace, sessionId, combineBitmapAndSpout);
    }
}
