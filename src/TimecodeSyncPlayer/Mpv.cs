using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer;

/// <summary>libmpv (mpv-2.dll) の最小限 P/Invoke ラッパー</summary>
internal static class Mpv
{
    private const string Lib = "mpv-2.dll";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpv_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_set_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    /// <summary>プロパティを double で取得。戻り値 0 = 成功</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_get_property(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format, out double result);

    /// <summary>プロパティを UTF-8 文字列で取得。呼び出し元で mpv_free() すること</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_get_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpv_free(IntPtr data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpv_command_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string args);

    internal const int FormatDouble = 5; // MPV_FORMAT_DOUBLE

    /// <summary>文字列プロパティを取得する。取得できなければ空文字列</summary>
    internal static string GetString(IntPtr ctx, string name)
    {
        IntPtr ptr = mpv_get_property_string(ctx, name);
        if (ptr == IntPtr.Zero) return "";
        try
        {
            return Marshal.PtrToStringUTF8(ptr) ?? "";
        }
        finally
        {
            mpv_free(ptr);
        }
    }
}
