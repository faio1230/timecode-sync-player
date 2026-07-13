namespace TimecodeSyncPlayer;

public enum RenderFramePublishSkipReason
{
    None,
    RenderFailed,
    NoDisplayableVideoSize
}

public sealed record RenderFramePublishDecision(
    bool ShouldPublish,
    RenderFramePublishSkipReason SkipReason);

public static class RenderFramePublishPolicy
{
    public static RenderFramePublishDecision Decide(int renderResultCode, bool hasDisplayableVideoSize)
    {
        if (renderResultCode < 0)
            return new RenderFramePublishDecision(false, RenderFramePublishSkipReason.RenderFailed);

        if (!hasDisplayableVideoSize)
            return new RenderFramePublishDecision(false, RenderFramePublishSkipReason.NoDisplayableVideoSize);

        return new RenderFramePublishDecision(true, RenderFramePublishSkipReason.None);
    }
}
