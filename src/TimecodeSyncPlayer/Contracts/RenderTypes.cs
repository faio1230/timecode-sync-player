using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer.Contracts;

/// <summary>
/// フレーム更新コールバック。受け取る側はデリゲートを
/// レンダーコンテキストと同じかそれ以上の生存期間のフィールドに保持すること。
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RenderUpdateFn(IntPtr callbackCtx);
