namespace TimecodeSyncPlayer;

internal readonly record struct TimelineViewportLayout(
    double TimeAxisHeight,
    double TimelineWidth,
    double TimelineHeight,
    double VisibleSeconds,
    double ScrollSeconds,
    double TrackHeight,
    int StartTrack,
    int EndTrack);

internal readonly record struct TimelineClipLayout(double X, double Width);

internal readonly record struct TimelineTimeTick(double Seconds, double X);

/// <summary>
/// タイムライン描画で使用する座標・可視範囲の純計算を担う。
/// WPF の描画オブジェクトや DPI 取得は扱わない。
/// </summary>
internal static class TimelineLayoutCalculator
{
    internal const double TimeAxisHeightPx = 20;
    internal const double MinClipWidthPx = 20;
    internal const double TickIntervalTargetPx = 50;
    internal const double HorizontalFollowMarginPx = 20;

    private static readonly double[] TimeAxisIntervals =
        [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600];

    internal static double CalculateZoomPivot(
        double playbackPositionSeconds,
        double horizontalScrollSeconds,
        double visibleSeconds)
    {
        return playbackPositionSeconds > horizontalScrollSeconds
            ? playbackPositionSeconds - horizontalScrollSeconds
            : visibleSeconds / 2;
    }

    internal static TimelineViewportLayout CalculateViewport(
        double width,
        double height,
        double dpiScaleY,
        double secondsPerPixel,
        double horizontalScrollSeconds,
        double trackHeight,
        int verticalScrollOffset,
        int trackCount)
    {
        double timeAxisHeight = TimeAxisHeightPx * dpiScaleY;
        double timelineWidth = width;
        double timelineHeight = height - timeAxisHeight;
        double visibleSeconds = timelineWidth * secondsPerPixel;
        double scaledTrackHeight = trackHeight * dpiScaleY;
        int endTrack = Math.Min(
            trackCount,
            verticalScrollOffset + (int)(timelineHeight / scaledTrackHeight) + 1);

        return new TimelineViewportLayout(
            timeAxisHeight,
            timelineWidth,
            timelineHeight,
            visibleSeconds,
            horizontalScrollSeconds,
            scaledTrackHeight,
            verticalScrollOffset,
            endTrack);
    }

    internal static double CalculateTrackY(
        int trackIndex,
        int startTrack,
        double timeAxisHeight,
        double trackHeight)
    {
        return timeAxisHeight + (trackIndex - startTrack) * trackHeight;
    }

    internal static TimelineClipLayout? CalculateClip(
        double clipStart,
        double clipDuration,
        bool hasKnownMediaDuration,
        double scrollSeconds,
        double visibleSeconds,
        double secondsPerPixel,
        double dpiScaleX)
    {
        double clipEnd = clipStart + clipDuration;
        if (clipEnd < scrollSeconds || clipStart > scrollSeconds + visibleSeconds)
            return null;

        double x = SecondsToX(clipStart, scrollSeconds, secondsPerPixel, dpiScaleX);
        double width = clipDuration / secondsPerPixel * dpiScaleX;
        if (!hasKnownMediaDuration)
        {
            width = Math.Max(MinClipWidthPx * dpiScaleX, width);
        }

        return new TimelineClipLayout(x, width);
    }

    internal static double SecondsToX(
        double seconds,
        double scrollSeconds,
        double secondsPerPixel,
        double dpiScaleX)
    {
        return (seconds - scrollSeconds) / secondsPerPixel * dpiScaleX;
    }

    internal static double GetTimeAxisInterval(double secondsPerPixel)
    {
        foreach (double interval in TimeAxisIntervals)
        {
            double pixelDistance = interval / secondsPerPixel;
            if (pixelDistance >= TickIntervalTargetPx)
                return interval;
        }

        return TimeAxisIntervals[^1];
    }

    internal static IReadOnlyList<TimelineTimeTick> CalculateTimeTicks(
        double scrollSeconds,
        double visibleSeconds,
        double secondsPerPixel,
        double dpiScaleX)
    {
        double interval = GetTimeAxisInterval(secondsPerPixel);
        double start = Math.Floor(scrollSeconds / interval) * interval;
        double end = scrollSeconds + visibleSeconds;
        var ticks = new List<TimelineTimeTick>();

        for (double t = start; t <= end; t += interval)
        {
            double x = SecondsToX(t, scrollSeconds, secondsPerPixel, dpiScaleX);
            ticks.Add(new TimelineTimeTick(t, x));
        }

        return ticks;
    }

    internal static double? CalculateHorizontalFollowScroll(
        double playbackPositionSeconds,
        double horizontalScrollSeconds,
        double secondsPerPixel,
        double dpiScaleX,
        double width)
    {
        double x = SecondsToX(
            playbackPositionSeconds,
            horizontalScrollSeconds,
            secondsPerPixel,
            dpiScaleX);

        if (x > width - HorizontalFollowMarginPx || x < HorizontalFollowMarginPx)
        {
            return playbackPositionSeconds - width * secondsPerPixel / 2;
        }

        return null;
    }

    internal static int? CalculateVerticalFollowOffset(
        int trackIndex,
        double height,
        double dpiScaleY,
        double trackHeight,
        int startTrack)
    {
        double timeAxisHeight = TimeAxisHeightPx * dpiScaleY;
        double visibleTracks = (height - timeAxisHeight) / trackHeight;
        int endTrack = startTrack + (int)visibleTracks;

        if (trackIndex < startTrack || trackIndex >= endTrack)
        {
            return Math.Max(0, trackIndex - (int)visibleTracks / 2);
        }

        return null;
    }

    internal static int? CalculateClickedTrackIndex(
        double x,
        double pointY,
        double dpiScaleY,
        double trackHeight,
        int verticalScrollOffset,
        int trackCount)
    {
        double timeAxisHeight = TimeAxisHeightPx * dpiScaleY;
        double y = pointY - timeAxisHeight;
        if (x < 0 || y < 0)
            return null;

        int trackIndex = verticalScrollOffset + (int)(y / trackHeight);
        if (trackIndex < 0 || trackIndex >= trackCount)
            return null;

        return trackIndex;
    }

    internal static double CalculateSeekTargetSeconds(
        double x,
        double dpiScaleX,
        double secondsPerPixel,
        double horizontalScrollSeconds,
        double trackTimelineInSeconds)
    {
        double clickSeconds = horizontalScrollSeconds + x / dpiScaleX * secondsPerPixel;
        return trackTimelineInSeconds + (clickSeconds - trackTimelineInSeconds);
    }
}
