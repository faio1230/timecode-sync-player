using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

internal static class RenderContextParameterBuilder
{
    public static RenderParam[] BuildSoftwareBackendParams(
        IMpvRenderApi mpvRenderApi,
        IntPtr apiTypeString)
    {
        return
        [
            new RenderParam
            {
                Type = mpvRenderApi.MpvRenderParamApiType,
                Data = apiTypeString
            },
            new RenderParam
            {
                Type = 0,
                Data = IntPtr.Zero
            }
        ];
    }
}
