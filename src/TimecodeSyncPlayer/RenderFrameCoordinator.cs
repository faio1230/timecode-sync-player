namespace TimecodeSyncPlayer;

internal sealed class RenderFrameCoordinator
{
    private readonly Func<RenderFrameSizeDecision> _decideSize;
    private readonly Action<int, int> _ensurePixelBuffer;
    private readonly Func<int, int, IntPtr> _buildRenderParameters;
    private readonly Func<MpvRenderFrameResult> _renderFrame;
    private readonly Func<int, bool, RenderFramePublishDecision> _decidePublish;
    private readonly Action<int> _logRenderFailure;
    private readonly Action<IntPtr, int, int, double> _publishFrame;

    public RenderFrameCoordinator(
        Func<RenderFrameSizeDecision> decideSize,
        Action<int, int> ensurePixelBuffer,
        Func<int, int, IntPtr> buildRenderParameters,
        Func<MpvRenderFrameResult> renderFrame,
        Func<int, bool, RenderFramePublishDecision> decidePublish,
        Action<int> logRenderFailure,
        Action<IntPtr, int, int, double> publishFrame)
    {
        _decideSize = decideSize;
        _ensurePixelBuffer = ensurePixelBuffer;
        _buildRenderParameters = buildRenderParameters;
        _renderFrame = renderFrame;
        _decidePublish = decidePublish;
        _logRenderFailure = logRenderFailure;
        _publishFrame = publishFrame;
    }

    public void Render()
    {
        RenderFrameSizeDecision sizeDecision = _decideSize();
        int width = sizeDecision.Width;
        int height = sizeDecision.Height;

        _ensurePixelBuffer(width, height);
        IntPtr pixelPointer = _buildRenderParameters(width, height);

        MpvRenderFrameResult renderResult = _renderFrame();
        RenderFramePublishDecision publishDecision = _decidePublish(
            renderResult.ReturnCode,
            sizeDecision.HasDisplayableVideoSize);

        if (publishDecision.SkipReason == RenderFramePublishSkipReason.RenderFailed)
        {
            _logRenderFailure(renderResult.ReturnCode);
            return;
        }

        if (!publishDecision.ShouldPublish)
            return;

        _publishFrame(pixelPointer, width, height, renderResult.ElapsedMs);
    }
}
