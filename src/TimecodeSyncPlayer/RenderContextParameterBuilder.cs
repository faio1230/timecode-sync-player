using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

internal static class RenderContextParameterBuilder
{
    public static MpvRenderNative.MpvRenderParam[] BuildSoftwareBackendParams(
        IMpvRenderApi mpvRenderApi,
        IntPtr apiTypeString)
    {
        return
        [
            new MpvRenderNative.MpvRenderParam
            {
                Type = mpvRenderApi.MpvRenderParamApiType,
                Data = apiTypeString
            },
            new MpvRenderNative.MpvRenderParam
            {
                Type = 0,
                Data = IntPtr.Zero
            }
        ];
    }
}
