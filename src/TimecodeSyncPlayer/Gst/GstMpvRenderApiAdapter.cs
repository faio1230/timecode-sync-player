using System;
using System.Runtime.InteropServices;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Gst;

/// <summary>
/// IMpvRenderApi の GStreamer 実装。SW レンダー（bgr0 CPU バッファ）と同じ形に
/// 最新のリースフレームをコピーし、WPF/Freeze 経路を無改造で動かす暫定プレビュー通路。
/// 定数値は mpv の SW レンダー API と同じ番号を使い、呼び出し側の生成する
/// MpvRenderParam 配列をそのまま解釈できるようにする。
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

    public int RenderContextRender(IntPtr ctx, RenderParam[] parameters)
    {
        if (ctx == IntPtr.Zero || parameters is null) return -1;

        IntPtr pointer = IntPtr.Zero;
        int stride = 0;
        int width = 0;
        int height = 0;
        string format = "bgr0";

        foreach (RenderParam p in parameters)
        {
            if (p.Type == 0) break;
            switch (p.Type)
            {
                case SwSizeParam:
                    width = Marshal.ReadInt32(p.Data);
                    height = Marshal.ReadInt32(p.Data, 4);
                    break;
                case SwStrideParam:
                    stride = (int)Marshal.ReadInt64(p.Data);
                    break;
                case SwPointerParam:
                    pointer = p.Data;
                    break;
                case SwFormatParam:
                    format = Marshal.PtrToStringAnsi(p.Data) ?? "bgr0";
                    break;
            }
        }

        if (pointer == IntPtr.Zero || stride <= 0 || width <= 0 || height <= 0)
            return -1;
        if (format != "bgr0" && format != "bgra")
            return -1; // shim の出す BGRA 並び (B,G,R,0) と異なる形式には対応しない

        return _state.RenderInto(pointer, stride, width, height);
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
