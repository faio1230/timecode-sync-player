namespace TimecodeSyncPlayer;

public sealed record RenderContextCreateResult(bool Success, IntPtr RenderContext, int ReturnCode)
{
    public static RenderContextCreateResult FromReturnCode(IntPtr renderContext, int rc)
    {
        return new RenderContextCreateResult(rc >= 0, renderContext, rc);
    }
}
