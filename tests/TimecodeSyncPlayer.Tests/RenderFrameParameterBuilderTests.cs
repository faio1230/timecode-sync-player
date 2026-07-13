using System.Runtime.InteropServices;
using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public class RenderFrameParameterBuilderTests
{
    [Fact]
    public void Build_UpdatesSizeStrideAndRenderParams()
    {
        using var bufferManager = new PixelBufferManager();
        bufferManager.InitFormatString("bgr0");
        bufferManager.InitStridePtr();
        bufferManager.EnsurePixelBuffer(320, 180);
        var renderParams = new MpvRenderNative.MpvRenderParam[5];
        var api = new FakeMpvRenderApi();

        IntPtr pixelPtr = RenderFrameParameterBuilder.Build(bufferManager, renderParams, api, 320, 180);

        pixelPtr.Should().Be(bufferManager.PixelPtr);
        bufferManager.SizeArray.Should().Equal(320, 180);
        Marshal.ReadInt64(bufferManager.StridePtr).Should().Be(320 * 4);
        renderParams[0].Type.Should().Be(api.MpvRenderParamSwSize);
        renderParams[0].Data.Should().Be(bufferManager.SizeArrayPtr);
        renderParams[1].Type.Should().Be(api.MpvRenderParamSwFormat);
        renderParams[1].Data.Should().Be(bufferManager.FormatStringPtr);
        renderParams[2].Type.Should().Be(api.MpvRenderParamSwStride);
        renderParams[2].Data.Should().Be(bufferManager.StridePtr);
        renderParams[3].Type.Should().Be(api.MpvRenderParamSwPointer);
        renderParams[3].Data.Should().Be(pixelPtr);
        renderParams[4].Type.Should().Be(0);
        renderParams[4].Data.Should().Be(IntPtr.Zero);
    }

    private sealed class FakeMpvRenderApi : IMpvRenderApi
    {
        public int MpvRenderParamApiType => 1;
        public int MpvRenderParamSwSize => 17;
        public int MpvRenderParamSwFormat => 18;
        public int MpvRenderParamSwStride => 19;
        public int MpvRenderParamSwPointer => 20;
        public string MpvRenderApiTypeSw => "sw";
        public ulong MpvRenderUpdateFrame => 1;
        public int RenderContextCreate(out IntPtr res, IntPtr mpv, MpvRenderNative.MpvRenderParam[] parameters)
        {
            res = IntPtr.Zero;
            return 0;
        }

        public ulong RenderContextUpdate(IntPtr ctx) => 0;
        public int RenderContextRender(IntPtr ctx, MpvRenderNative.MpvRenderParam[] parameters) => 0;
        public void RenderContextSetUpdateCallback(IntPtr ctx, MpvRenderNative.MpvRenderUpdateFn callback, IntPtr callbackCtx) { }
        public void RenderContextFree(IntPtr ctx) { }
    }
}
