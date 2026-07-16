using System.Runtime.InteropServices;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

internal static class RenderFrameParameterBuilder
{
    public static IntPtr Build(
        PixelBufferManager bufferManager,
        MpvRenderNative.MpvRenderParam[] renderParams,
        IMpvRenderApi mpvRenderApi,
        int width,
        int height)
    {
        FrameBufferSize.GetRequiredByteCount(width, height);
        bufferManager.SizeArray[0] = width;
        bufferManager.SizeArray[1] = height;
        Marshal.WriteInt64(bufferManager.StridePtr, (long)(width * 4));
        IntPtr pixelPtr = bufferManager.PixelPtr;

        renderParams[0] = new MpvRenderNative.MpvRenderParam { Type = mpvRenderApi.MpvRenderParamSwSize, Data = bufferManager.SizeArrayPtr };
        renderParams[1] = new MpvRenderNative.MpvRenderParam { Type = mpvRenderApi.MpvRenderParamSwFormat, Data = bufferManager.FormatStringPtr };
        renderParams[2] = new MpvRenderNative.MpvRenderParam { Type = mpvRenderApi.MpvRenderParamSwStride, Data = bufferManager.StridePtr };
        renderParams[3] = new MpvRenderNative.MpvRenderParam { Type = mpvRenderApi.MpvRenderParamSwPointer, Data = pixelPtr };
        renderParams[4] = new MpvRenderNative.MpvRenderParam { Type = 0, Data = IntPtr.Zero };

        return pixelPtr;
    }
}
