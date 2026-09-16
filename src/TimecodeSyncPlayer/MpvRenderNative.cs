using System.Runtime.InteropServices;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

/// <summary>
/// libmpv のレンダーコンテキスト API の最小限 P/Invoke。
/// SW（ソフトウェア）レンダーバックエンドを使用してピクセルバッファに出力する。
/// パラメータ構造体とコールバック型は中立な <see cref="RenderParam"/> /
/// <see cref="RenderUpdateFn"/> を使う。
/// </summary>
public static class MpvRenderNative
{
    private const string Lib = "mpv-2.dll";

    // mpv_render_param_type 定数
    internal const int MPV_RENDER_PARAM_API_TYPE   = 1;
    internal const int MPV_RENDER_PARAM_SW_SIZE    = 17;
    internal const int MPV_RENDER_PARAM_SW_FORMAT  = 18;
    internal const int MPV_RENDER_PARAM_SW_STRIDE  = 19;
    internal const int MPV_RENDER_PARAM_SW_POINTER = 20;

    /// <summary>SW バックエンドを指定する文字列。StringToHGlobalAnsi で変換して使う。</summary>
    internal const string MPV_RENDER_API_TYPE_SW = "sw";

    /// <summary>mpv_render_context_update の戻り値フラグ: 新しいフレームが準備できた</summary>
    internal const ulong MPV_RENDER_UPDATE_FRAME = 1ul;

    /// <summary>レンダーコンテキストを作成する。戻り値 0 = 成功。</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_render_context_create(
        out IntPtr res, IntPtr mpv, RenderParam[] parameters);

    /// <summary>新しいフレームが利用可能かどうかを示すフラグを返す。</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong mpv_render_context_update(IntPtr ctx);

    /// <summary>ピクセルバッファにフレームをレンダーする。</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_render_context_render(
        IntPtr ctx, RenderParam[] parameters);

    /// <summary>
    /// フレーム更新コールバックを登録する。
    /// コールバックは mpv 内部スレッドから呼ばれるため、
    /// 呼び出し元で Dispatcher.BeginInvoke に渡すこと。
    /// <para>
    /// <b>重要:</b> 渡した <see cref="RenderUpdateFn"/> デリゲートを
    /// レンダーコンテキストと同じかそれ以上の生存期間のフィールドに保持すること。
    /// ローカル変数のみで保持すると GC に回収されクラッシュする。
    /// </para>
    /// </summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_render_context_set_update_callback(
        IntPtr ctx, RenderUpdateFn callback, IntPtr callbackCtx);

    /// <summary>レンダーコンテキストを解放する。mpv_terminate_destroy の前に呼ぶこと。</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_render_context_free(IntPtr ctx);
}
