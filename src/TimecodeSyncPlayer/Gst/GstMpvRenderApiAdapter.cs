using System;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Gst;

/// <summary>
/// IMpvRenderApi の GStreamer 実装。合成は GPU 合成層が shim のリースから直接行い、
/// ここはフレーム通知（update callback）の生成と寿命管理だけを担う。
/// 定数値は mpv の SW レンダー API と同じ番号を使い、呼び出し側の生成する
/// RenderParam 配列をそのまま解釈できるようにする。
/// </summary>
internal sealed class GstMpvRenderApiAdapter : IMpvRenderApi
{
    private const int ApiTypeParam = 1;
    private const int SwSizeParam = 17;
    private const int SwFormatParam = 18;
    private const int SwStrideParam = 19;
    private const int SwPointerParam = 20;

    private readonly GstBackendState _state;

    public GstMpvRenderApiAdapter(GstBackendState state)
    {
        _state = state;
    }

    public int MpvRenderParamApiType => ApiTypeParam;
    public int MpvRenderParamSwSize => SwSizeParam;
    public int MpvRenderParamSwFormat => SwFormatParam;
    public int MpvRenderParamSwStride => SwStrideParam;
    public int MpvRenderParamSwPointer => SwPointerParam;
    public string MpvRenderApiTypeSw => "sw";
    public ulong MpvRenderUpdateFrame => 1ul;

    public int RenderContextCreate(out IntPtr res, IntPtr mpv, RenderParam[] parameters)
    {
        // 「SW バックエンド」の宣言はこの経路では検証のみ（GStreamer 側は常に sw 互換出力）。
        res = mpv;
        return mpv != IntPtr.Zero ? 0 : -1;
    }

    public ulong RenderContextUpdate(IntPtr ctx)
    {
        if (ctx == IntPtr.Zero) return 0;
        return _state.Native.ConsumeUpdate(ctx) != 0 ? MpvRenderUpdateFrame : 0ul;
    }

    public void RenderContextSetUpdateCallback(
        IntPtr ctx, RenderUpdateFn callback, IntPtr callbackCtx)
    {
        if (callback is null)
            _state.DetachRenderCallback();
        else
            _state.AttachRenderCallback(callback, callbackCtx);
    }

    public void RenderContextFree(IntPtr ctx)
    {
        _state.DetachRenderCallback();
    }
}
