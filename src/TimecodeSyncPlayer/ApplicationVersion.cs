using System.Reflection;

namespace TimecodeSyncPlayer;

internal static class ApplicationVersion
{
    private const string ProductTitle = "Timecode Sync Player";

    public static string Current { get; } = ReadFrom(typeof(App).Assembly);

    public static string WindowTitle => $"{ProductTitle} v{Current}";

    internal static string ReadFrom(Assembly assembly)
    {
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return Normalize(informationalVersion, assembly.GetName().Version);
    }

    internal static string Normalize(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            int metadataSeparator = informationalVersion.IndexOf('+');
            return metadataSeparator >= 0
                ? informationalVersion[..metadataSeparator]
                : informationalVersion;
        }

        return assemblyVersion is null
            ? "0.0.0"
            : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(assemblyVersion.Build, 0)}";
    }
}
