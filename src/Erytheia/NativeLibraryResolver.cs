using System.Reflection;
using System.Runtime.InteropServices;

namespace Erytheia;

/// <summary>
/// アセンブリ共通の DllImport 解決。GStreamer（shim / ランタイム DLL）の振り分けを
/// 中立な名前でここに置く。個別のロードは GstNativeLibraryResolver が行う。
/// </summary>
internal static class NativeLibraryResolver
{
    public static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(App).Assembly, Resolve);

    private static IntPtr Resolve(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
        => Erytheia.Gst.GstNative.IsGstLibrary(libraryName)
            ? Erytheia.Gst.GstNativeLibraryResolver.ResolveLibrary(
                libraryName, assembly, searchPath)
            : IntPtr.Zero;
}
