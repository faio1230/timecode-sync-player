using System.Reflection;
using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer;

/// <summary>
/// アセンブリ共通の DllImport 解決。GStreamer（shim / ランタイム DLL）の
/// 振り分けを中立な名前でここに置き、それ以外は mpv の候補名で解決する
/// （mpv 側の解決は段 2 の後半で削除する）。
/// </summary>
internal static class NativeLibraryResolver
{
    public static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(App).Assembly, Resolve);

    private static IntPtr Resolve(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (TimecodeSyncPlayer.Gst.GstNative.IsGstLibrary(libraryName))
            return TimecodeSyncPlayer.Gst.GstNativeLibraryResolver.ResolveLibrary(
                libraryName, assembly, searchPath);

        foreach (string candidate in MpvLibraryNameResolver.GetCandidates(libraryName))
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out IntPtr handle))
                return handle;
        }

        return IntPtr.Zero;
    }
}
