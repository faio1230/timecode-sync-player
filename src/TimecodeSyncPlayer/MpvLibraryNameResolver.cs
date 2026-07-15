using System.Reflection;
using System.Runtime.InteropServices;

namespace TimecodeSyncPlayer;

internal static class MpvLibraryNameResolver
{
    internal const string ImportedLibraryName = "mpv-2.dll";

    public static IReadOnlyList<string> GetCandidates(string libraryName) =>
        string.Equals(libraryName, ImportedLibraryName, StringComparison.Ordinal)
            ? [ImportedLibraryName, "libmpv-2.dll"]
            : [];
}

internal static class MpvNativeLibraryResolver
{
    public static void Register() =>
        NativeLibrary.SetDllImportResolver(typeof(App).Assembly, Resolve);

    private static IntPtr Resolve(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        foreach (string candidate in MpvLibraryNameResolver.GetCandidates(libraryName))
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out IntPtr handle))
                return handle;
        }

        return IntPtr.Zero;
    }
}
