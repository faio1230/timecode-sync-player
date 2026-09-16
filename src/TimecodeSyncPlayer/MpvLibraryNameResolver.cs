namespace TimecodeSyncPlayer;

/// <summary>
/// mpv の候補 DLL 名（段 2 の後半で削除予定）。GStreamer の振り分けは
/// <see cref="NativeLibraryResolver"/> が持つ。
/// </summary>
internal static class MpvLibraryNameResolver
{
    internal const string ImportedLibraryName = "mpv-2.dll";

    public static IReadOnlyList<string> GetCandidates(string libraryName) =>
        string.Equals(libraryName, ImportedLibraryName, StringComparison.Ordinal)
            ? [ImportedLibraryName, "libmpv-2.dll"]
            : [];
}
