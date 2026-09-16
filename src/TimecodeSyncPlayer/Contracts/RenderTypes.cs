using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer.Contracts;

/// <summary>
/// レンダー API へ渡すパラメータ。
/// C 定義: { enum(int) type; void* data; }。x64 では int(4) + padding(4) + ptr(8) = 16 バイト。
/// 明示的パディングフィールドで CLR のデフォルトアライメントに依存しないようにする。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RenderParam
{
    internal int Type;
#pragma warning disable CS0169
    private int _padding;   // Data を offset 8 に整列させる明示的パディング
#pragma warning restore CS0169
    internal IntPtr Data;
}

/// <summary>
/// フレーム更新コールバック。受け取る側はデリゲートを
/// レンダーコンテキストと同じかそれ以上の生存期間のフィールドに保持すること。
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RenderUpdateFn(IntPtr callbackCtx);
