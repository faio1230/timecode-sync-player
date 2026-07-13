namespace TimecodeSyncPlayer;

internal static class TimecodeDisplayFormatter
{
    public static string FpsLabel(double fps, bool dropFrame) =>
        fps switch
        {
            24.0 => "24 fps",
            25.0 => "25 fps",
            _ => dropFrame ? "29.97 fps (DF)" : "30 fps"
        };

    public static string RealTime(double totalSeconds)
    {
        if (totalSeconds < 0)
            totalSeconds = 0;
        return $"{totalSeconds:F3} s";
    }
}
