using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Gst;

namespace TimecodeSyncPlayer.Tests.Gst;

public class GstRenderUpdateSourceTests
{
    [Fact]
    public void TryCreateContext_UsesPlayerHandle()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1) };
        var state = new GstBackendState(native);
        state.EnsurePlayer().Should().BeTrue();
        var source = new GstRenderUpdateSource(state);

        source.TryCreateContext(state.Player, out IntPtr context).Should().BeTrue();
        context.Should().Be(state.Player);

        source.TryCreateContext(IntPtr.Zero, out IntPtr none).Should().BeFalse();
        none.Should().Be(IntPtr.Zero);
    }

    [Fact]
    public void ConsumeUpdate_MapsFrameFlag()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1), ConsumeUpdateResult = 1 };
        var state = new GstBackendState(native);
        state.EnsurePlayer().Should().BeTrue();
        var source = new GstRenderUpdateSource(state);

        source.ConsumeUpdate(state.Player).Should().Be(source.FrameUpdateFlag);

        native.ConsumeUpdateResult = 0;
        source.ConsumeUpdate(state.Player).Should().Be(0ul);
        source.ConsumeUpdate(IntPtr.Zero).Should().Be(0ul);
    }

    [Fact]
    public void SetUpdateCallback_AttachesRetainsAndDetaches()
    {
        var native = new FakeGstNative { PlayerCreateResult = new IntPtr(1) };
        var state = new GstBackendState(native);
        state.EnsurePlayer().Should().BeTrue();
        var source = new GstRenderUpdateSource(state);
        bool invoked = false;
        RenderUpdateFn callback = _ => invoked = true;

        source.SetUpdateCallback(state.Player, callback);
        native.SetFrameCallbackCalls.Should().Be(1);
        native.LastFrameCallback.Should().NotBeNull();

        // RenderUpdateFn の契約: ネイティブ通知が登録デリゲートへ届く。
        native.LastFrameCallback!(IntPtr.Zero, 1, 1);
        invoked.Should().BeTrue();

        source.SetUpdateCallback(state.Player, null);
        native.SetFrameCallbackCalls.Should().Be(2);
        native.LastFrameCallback.Should().BeNull();

        source.FreeContext(state.Player);
        native.SetFrameCallbackCalls.Should().Be(2, "解除済みならネイティブへ再度触らない");
    }
}
