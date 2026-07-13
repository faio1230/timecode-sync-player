using System.Runtime.InteropServices;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

internal sealed class MpvRenderApi : IMpvRenderApi
{
    public int MpvRenderParamApiType => MpvRenderNative.MPV_RENDER_PARAM_API_TYPE;
    public int MpvRenderParamSwSize => MpvRenderNative.MPV_RENDER_PARAM_SW_SIZE;
    public int MpvRenderParamSwFormat => MpvRenderNative.MPV_RENDER_PARAM_SW_FORMAT;
    public int MpvRenderParamSwStride => MpvRenderNative.MPV_RENDER_PARAM_SW_STRIDE;
    public int MpvRenderParamSwPointer => MpvRenderNative.MPV_RENDER_PARAM_SW_POINTER;
    public string MpvRenderApiTypeSw => MpvRenderNative.MPV_RENDER_API_TYPE_SW;
    public ulong MpvRenderUpdateFrame => MpvRenderNative.MPV_RENDER_UPDATE_FRAME;

    public int RenderContextCreate(out IntPtr res, IntPtr mpv, MpvRenderNative.MpvRenderParam[] parameters)
        => MpvRenderNative.mpv_render_context_create(out res, mpv, parameters);

    public ulong RenderContextUpdate(IntPtr ctx)
        => MpvRenderNative.mpv_render_context_update(ctx);

    public int RenderContextRender(IntPtr ctx, MpvRenderNative.MpvRenderParam[] parameters)
        => MpvRenderNative.mpv_render_context_render(ctx, parameters);

    public void RenderContextSetUpdateCallback(IntPtr ctx, MpvRenderNative.MpvRenderUpdateFn callback, IntPtr callbackCtx)
        => MpvRenderNative.mpv_render_context_set_update_callback(ctx, callback, callbackCtx);

    public void RenderContextFree(IntPtr ctx)
        => MpvRenderNative.mpv_render_context_free(ctx);
}
