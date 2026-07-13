namespace TimecodeSyncPlayer;

internal static class PlaylistTrackExtensions
{
    public static TimeSpan CalculateTimelineOut(this PlaylistTrack track)
    {
        return track.GetActualTimelineIn() + track.GetEffectiveDuration();
    }

    public static string GetTimelineRangeText(this PlaylistTrack track)
    {
        var actualIn = track.GetActualTimelineIn();
        var actualOut = track.GetActualTimelineOut();
        return $"{FormatTime(actualIn)} → {FormatTime(actualOut)}";
    }

    private static string FormatTime(TimeSpan ts)
    {
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }
}
