using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace TimecodeSyncPlayer.Output;

/// <summary>
/// 借用テクスチャポインタ（GStreamer shim のリースなど）を D3D11 で扱う補助。
/// COM の所有権（AddRef）と、Freeze 用の生ポインタコピーを提供する。
/// </summary>
internal static class NativeTextureOps
{
    // ID3D11DeviceContext の vtable: IUnknown(0-2) + ID3D11DeviceChild(GetDevice 等 4 つ) の後に
    // VSSetConstantBuffers(7) ... CopySubresourceRegion(46), CopyResource(47), UpdateSubresource(48)。
    // void CopyResource(ID3D11Resource* pDstResource, ID3D11Resource* pSrcResource)
    private const int CopyResourceSlot = 47;

    /// <summary>
    /// 借用 COM ポインタの参照カウントを1つ取り、所有ラッパーとして返す。
    /// Vortice/SharpGen の IntPtr コンストラクタは Release 所有権を仮定するため、
    /// AddRef してから包むことで Dispose と釣り合わせる。
    /// </summary>
    public static T OpenOwned<T>(IntPtr pointer, Func<IntPtr, T> wrap) where T : IDisposable
    {
        if (pointer == IntPtr.Zero) throw new ArgumentException("Pointer must not be zero.", nameof(pointer));
        Marshal.AddRef(pointer);
        try { return wrap(pointer); }
        catch { Marshal.Release(pointer); throw; }
    }

    public static unsafe void CopyResource(ID3D11DeviceContext context, IntPtr destination, IntPtr source)
    {
        var method = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)(*(IntPtr**)context.NativePointer)[CopyResourceSlot];
        method(context.NativePointer, destination, source);
    }

    public static void CopyResource(ID3D11DeviceContext context, ID3D11Texture2D destination, ID3D11Texture2D source)
        => CopyResource(context, destination.NativePointer, source.NativePointer);
}
