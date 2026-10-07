namespace TimecodeSyncPlayer;

internal static class PlaylistTrackExtensions
{
    /// <summary>v0.6.6 F-7: 長さが分からない行の範囲の終わり（mm:ss の欄）。</summary>
    public const string UnknownRangeEndText = "--:--";

    public static TimeSpan CalculateTimelineOut(this PlaylistTrack track)
    {
        return track.GetActualTimelineIn() + track.GetEffectiveDuration();
    }

    public static string GetTimelineRangeText(this PlaylistTrack track)
    {
        var actualIn = track.GetActualTimelineIn();
        var actualOut = track.GetActualTimelineOut();
        // v0.6.6 F-7: 長さが分からない行は終わりを不明と見せる（0 を長さとして見せない）。
        string outText = PlaylistTrackFormatter.IsDurationUnknown(track) ? UnknownRangeEndText : FormatTime(actualOut);
        return $"{FormatTime(actualIn)} → {outText}";
    }

    private static string FormatTime(TimeSpan ts)
    {
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }
}
