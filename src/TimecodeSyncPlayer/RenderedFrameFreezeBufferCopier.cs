namespace TimecodeSyncPlayer;

public sealed class RenderedFrameFreezeBufferCopier
{
    private readonly PixelBufferManager _bufferManager;

    public RenderedFrameFreezeBufferCopier(PixelBufferManager bufferManager)
    {
        _bufferManager = bufferManager;
    }

    internal bool CopyIfNeeded(GapState gapState, int width, int height)
    {
        _bufferManager.EnsureFrozenFrameBuffer(width, height);
        if (!ContinueModePlaybackPolicy.ShouldCopyRenderedFrameToFreezeBuffer(gapState) ||
            _bufferManager.FrozenFrameBuffer == null)
        {
            return false;
        }

        _bufferManager.CopyToFrozenFrame(width, height);
        return true;
    }
}
