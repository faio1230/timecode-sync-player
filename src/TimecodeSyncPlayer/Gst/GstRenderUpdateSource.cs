using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Gst;

/// <summary>
/// IRenderUpdateSource の GStreamer 実装。フレーム通知とコンテキスト寿命だけを担い、
/// コールバックの配線は GstBackendState（shim 所有者）へ委譲する。
/// </summary>
internal sealed class GstRenderUpdateSource : IRenderUpdateSource
{
    private readonly GstBackendState _state;

    public GstRenderUpdateSource(GstBackendState state)
    {
        _state = state;
    }

    public ulong FrameUpdateFlag => 1ul;

    public bool TryCreateContext(IntPtr player, out IntPtr context)
    {
        context = player;
        return player != IntPtr.Zero;
    }

    public ulong ConsumeUpdate(IntPtr context)
    {
        if (context == IntPtr.Zero) return 0ul;
        return _state.Native.ConsumeUpdate(context) != 0 ? FrameUpdateFlag : 0ul;
    }

    public void SetUpdateCallback(IntPtr context, RenderUpdateFn? callback)
    {
        if (callback is null)
            _state.DetachRenderCallback();
        else
            _state.AttachRenderCallback(callback, IntPtr.Zero);
    }

    public void FreeContext(IntPtr context)
    {
        _state.DetachRenderCallback();
    }
}
