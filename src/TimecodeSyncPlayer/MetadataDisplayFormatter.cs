namespace TimecodeSyncPlayer;

internal static class MetadataDisplayFormatter
{
    public static string FormatMetadataLine(int width, int height, double fps, string videoCodec, string audioCodec)
    {
        string resolution = width > 0 && height > 0 ? $"{width}x{height}" : "";
        string fpsText = fps > 0 ? $"{fps:F3} fps" : "";
        string video = videoCodec.Length > 0 ? $"V:{videoCodec}" : "";
        string audio = audioCodec.Length > 0 ? $"A:{audioCodec}" : "";

        var parts = new[] { resolution, fpsText, video, audio };
        return string.Join("  ", System.Array.FindAll(parts, static part => part.Length > 0));
    }

    public static string FormatOsdText(string timeText, string metadataLine) =>
        metadataLine.Length > 0 ? $"{timeText}\n{metadataLine}" : timeText;
}
