namespace TimecodeSyncPlayer;

internal static class PlaybackTimeFormatter
{
    public static string FormatFrames(double seconds, double fps)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        int hours = (int)(seconds / 3600);
        int minutes = (int)(seconds % 3600 / 60);
        int wholeSeconds = (int)(seconds % 60);
        int frames = fps > 0 ? (int)((seconds % 1.0) * fps) : 0;

        return $"{hours}:{minutes:D2}:{wholeSeconds:D2}:{frames:D2}";
    }
}
