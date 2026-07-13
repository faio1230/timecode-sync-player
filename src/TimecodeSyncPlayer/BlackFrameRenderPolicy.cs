namespace TimecodeSyncPlayer;

internal static class BlackFrameRenderPolicy
{
    public static (int Width, int Height) ResolveSize(int videoWidth, int videoHeight)
    {
        int width = videoWidth > 0 ? videoWidth : 16;
        int height = videoHeight > 0 ? videoHeight : 16;
        return (width, height);
    }
}
