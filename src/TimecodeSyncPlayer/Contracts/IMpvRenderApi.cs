using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer.Contracts;

public interface IMpvRenderApi
{
    int MpvRenderParamApiType { get; }
    int MpvRenderParamSwSize { get; }
    int MpvRenderParamSwFormat { get; }
    int MpvRenderParamSwStride { get; }
    int MpvRenderParamSwPointer { get; }
    string MpvRenderApiTypeSw { get; }
    ulong MpvRenderUpdateFrame { get; }

    int RenderContextCreate(out IntPtr res, IntPtr mpv, RenderParam[] parameters);
    ulong RenderContextUpdate(IntPtr ctx);
    void RenderContextSetUpdateCallback(IntPtr ctx, RenderUpdateFn callback, IntPtr callbackCtx);
    void RenderContextFree(IntPtr ctx);
}
