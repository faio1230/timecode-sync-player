namespace TimecodeSyncPlayer;

internal static class DebugOsdPolicy
{
    public static bool ShouldWrite(bool showDebugOsd) => showDebugOsd;

    public static string FormatText(string timeText, string metadataLine) =>
        MetadataDisplayFormatter.FormatOsdText(timeText, metadataLine);
}
