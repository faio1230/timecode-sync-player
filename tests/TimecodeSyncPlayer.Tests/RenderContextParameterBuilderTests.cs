using FluentAssertions;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests;

public class RenderContextParameterBuilderTests
{
    [Fact]
    public void BuildSoftwareBackendParams_CreatesApiTypeAndTerminator()
    {
        var api = new FakeMpvRenderApi();
        IntPtr sw = new(123);

        MpvRenderNative.MpvRenderParam[] parameters = RenderContextParameterBuilder.BuildSoftwareBackendParams(api, sw);

        parameters.Should().HaveCount(2);
        parameters[0].Type.Should().Be(api.MpvRenderParamApiType);
        parameters[0].Data.Should().Be(sw);
        parameters[1].Type.Should().Be(0);
        parameters[1].Data.Should().Be(IntPtr.Zero);
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
