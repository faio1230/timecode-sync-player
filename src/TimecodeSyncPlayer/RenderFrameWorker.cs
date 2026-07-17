namespace TimecodeSyncPlayer;

internal sealed record RenderFrameWorkerResult(
    bool ShouldPublish,
    IntPtr Pixels,
    int Width,
    int Height,
    double RenderMs)
{
    public static RenderFrameWorkerResult NotPublished { get; } =
        new(false, IntPtr.Zero, 0, 0, 0);
}

internal sealed class RenderFrameWorker
{
    private readonly Action<int, int> _ensurePixelBuffer;
    private readonly Func<int, int, IntPtr> _buildRenderParameters;
    private readonly Func<MpvRenderFrameResult> _renderFrame;
    private readonly Func<int, bool, RenderFramePublishDecision> _decidePublish;
    private readonly Action<int> _logRenderFailure;

    public RenderFrameWorker(
        Action<int, int> ensurePixelBuffer,
        Func<int, int, IntPtr> buildRenderParameters,
        Func<MpvRenderFrameResult> renderFrame,
        Func<int, bool, RenderFramePublishDecision> decidePublish,
        Action<int> logRenderFailure)
    {
        _ensurePixelBuffer = ensurePixelBuffer;
        _buildRenderParameters = buildRenderParameters;
        _renderFrame = renderFrame;
        _decidePublish = decidePublish;
        _logRenderFailure = logRenderFailure;
    }

    public RenderFrameWorkerResult Execute(RenderFrameSizeDecision sizeDecision)
    {
        if (!sizeDecision.ShouldRender)
            return RenderFrameWorkerResult.NotPublished;

        int width = sizeDecision.Width;
        int height = sizeDecision.Height;
        _ensurePixelBuffer(width, height);
        IntPtr pixels = _buildRenderParameters(width, height);

        MpvRenderFrameResult renderResult = _renderFrame();
        RenderFramePublishDecision publishDecision = _decidePublish(
            renderResult.ReturnCode,
            sizeDecision.HasDisplayableVideoSize);
        if (publishDecision.SkipReason == RenderFramePublishSkipReason.RenderFailed)
        {
            _logRenderFailure(renderResult.ReturnCode);
            return RenderFrameWorkerResult.NotPublished;
        }

        if (!publishDecision.ShouldPublish)
            return RenderFrameWorkerResult.NotPublished;

        return new RenderFrameWorkerResult(
            true,
            pixels,
            width,
            height,
            renderResult.ElapsedMs);
    }
}
