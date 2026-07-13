namespace TimecodeSyncPlayer;

internal sealed record RenderFrameSizeDecision(
    int Width,
    int Height,
    bool HasDisplayableVideoSize);

internal static class RenderFrameSizePolicy
{
    public static RenderFrameSizeDecision Decide(int videoWidth, int videoHeight, int fallbackSize)
    {
        if (videoWidth > 0 && videoHeight > 0)
            return new RenderFrameSizeDecision(videoWidth, videoHeight, HasDisplayableVideoSize: true);

        return new RenderFrameSizeDecision(fallbackSize, fallbackSize, HasDisplayableVideoSize: false);
    }
}
