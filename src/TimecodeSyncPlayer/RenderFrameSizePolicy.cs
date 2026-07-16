using Serilog;

namespace TimecodeSyncPlayer;

internal sealed record RenderFrameSizeDecision(
    int Width,
    int Height,
    bool HasDisplayableVideoSize,
    bool ShouldRender = true);

internal static class RenderFrameSizePolicy
{
    public static RenderFrameSizeDecision Decide(int videoWidth, int videoHeight, int fallbackSize)
    {
        if (FrameBufferSize.TryGetRequiredByteCount(videoWidth, videoHeight, out _))
            return new RenderFrameSizeDecision(videoWidth, videoHeight, HasDisplayableVideoSize: true);

        Log.Warning(
            "RenderFrame: invalid video dimensions {Width}x{Height}; frame rendering skipped",
            videoWidth,
            videoHeight);
        return new RenderFrameSizeDecision(
            fallbackSize,
            fallbackSize,
            HasDisplayableVideoSize: false,
            ShouldRender: false);
    }
}
