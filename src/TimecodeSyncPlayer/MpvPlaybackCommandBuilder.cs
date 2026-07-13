using System.Globalization;

namespace TimecodeSyncPlayer;

internal static class MpvPlaybackCommandBuilder
{
    public static string BuildLoadFileCommand(string path, double? startPosition)
    {
        string escapedPath = EscapeCommandArg(path.Replace('\\', '/'));
        if (!startPosition.HasValue)
            return $"no-osd loadfile \"{escapedPath}\" replace";

        string start = startPosition.Value.ToString("F6", CultureInfo.InvariantCulture);
        return $"no-osd loadfile \"{escapedPath}\" replace -1 start={start}";
    }

    public static string EscapeCommandArg(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
