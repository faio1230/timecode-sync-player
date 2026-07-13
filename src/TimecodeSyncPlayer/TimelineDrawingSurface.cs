using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TimecodeSyncPlayer;

public class TimelineDrawingSurface : FrameworkElement, IDisposable
{
    private bool _disposed;
    private readonly PlaylistState _playlist;
    private readonly TimelineDisplayState _displayState;
    private readonly DrawingVisual _drawingVisual;
    private readonly DrawingVisual _playheadVisual;
    private readonly TextBlock _emptyMessage;
    private Guid? _loadedTrackId;
    private double _playbackPositionSeconds;
    private DateTime _lastVerticalScrollAt = DateTime.MinValue;
    private const double VerticalFollowCooldownMs = 500;
    private const double TimeAxisHeightPx = 20;
    private const double MinClipWidthPx = 20;
    private const double TextWidthThresholdPx = 30;
    private const double TickIntervalTargetPx = 50;

    internal event EventHandler<TimelineSeekEventArgs>? TimelineSeekRequested;

    public TimelineDrawingSurface(PlaylistState playlist, bool isTimelineVisible)
    {
        _playlist = playlist;
        _displayState = new TimelineDisplayState { IsVisible = isTimelineVisible };
        _drawingVisual = new DrawingVisual();
        _playheadVisual = new DrawingVisual();

        AddVisualChild(_drawingVisual);
        AddVisualChild(_playheadVisual);

        _emptyMessage = new TextBlock
        {
            Text = "クリップを追加してください",
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        AddVisualChild(_emptyMessage);

        _playlist.Tracks.CollectionChanged += OnTracksCollectionChanged;
        MouseLeftButtonUp += TimelineDrawingSurface_MouseLeftButtonUp;

        UpdateEmptyMessageVisibility();
    }

    protected override int VisualChildrenCount => 3;

    protected override Visual GetVisualChild(int index)
    {
        return index switch
        {
            0 => _drawingVisual,
            1 => _playheadVisual,
            2 => _emptyMessage,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _emptyMessage.Measure(availableSize);
        return new Size(
            Math.Min(availableSize.Width, double.MaxValue),
            Math.Min(availableSize.Height, double.MaxValue));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _emptyMessage.Arrange(new Rect(finalSize));
        Dispatcher.BeginInvoke(new Action(RenderClips));
        return finalSize;
    }

    private void OnTracksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(RenderClips));
        UpdateEmptyMessageVisibility();
    }

    private void UpdateEmptyMessageVisibility()
    {
        _emptyMessage.Visibility = _playlist.Tracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ZoomIn()
    {
        double width = ActualWidth;
        if (width <= 0) return;

        double pivotSeconds = _playbackPositionSeconds > _displayState.HorizontalScrollSeconds
            ? _playbackPositionSeconds - _displayState.HorizontalScrollSeconds
            : _displayState.VisibleSeconds(width) / 2;
        _displayState.ZoomIn(pivotSeconds);
        RenderClips();
    }

    public void ZoomOut()
    {
        double width = ActualWidth;
        if (width <= 0) return;

        double pivotSeconds = _playbackPositionSeconds > _displayState.HorizontalScrollSeconds
            ? _playbackPositionSeconds - _displayState.HorizontalScrollSeconds
            : _displayState.VisibleSeconds(width) / 2;
        _displayState.ZoomOut(pivotSeconds);
        RenderClips();
    }

    public void ScrollHorizontal(double seconds)
    {
        _displayState.ScrollHorizontal(seconds);
        RenderClips();
    }

    public void ScrollVertical(int tracks)
    {
        _lastVerticalScrollAt = DateTime.Now;
        _displayState.ScrollVertical(tracks);
        RenderClips();
    }

    public void SetTrackHeight(double height)
    {
        _displayState.TrackHeight = height;
        RenderClips();
    }

    public bool IsTimelineVisible
    {
        get => _displayState.IsVisible;
        set
        {
            if (_displayState.IsVisible != value)
            {
                _displayState.IsVisible = value;
                RenderClips();
                RenderPlayhead();
            }
        }
    }

    public Guid? LoadedTrackId
    {
        get => _loadedTrackId;
        set
        {
            if (_loadedTrackId != value)
            {
                _loadedTrackId = value;
                _lastVerticalScrollAt = DateTime.MinValue;
            }
        }
    }

    internal TimelineDisplayState DisplayState => _displayState;

    private void RenderClips()
    {
        using DrawingContext dc = _drawingVisual.RenderOpen();

        if (!_displayState.IsVisible || _playlist.Tracks.Count == 0)
            return;

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double dpiScaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double dpiScaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        double timeAxisHeight = TimeAxisHeightPx * dpiScaleY;
        double timelineWidth = width;
        double timelineHeight = height - timeAxisHeight;
        double visibleSeconds = _displayState.VisibleSeconds(timelineWidth);
        double scrollSeconds = _displayState.HorizontalScrollSeconds;
        double trackHeight = _displayState.TrackHeight * dpiScaleY;

        int startTrack = _displayState.VerticalScrollOffset;
        int endTrack = Math.Min(_playlist.Tracks.Count, startTrack + (int)(timelineHeight / trackHeight) + 1);

        for (int i = startTrack; i < endTrack; i++)
        {
            var track = _playlist.Tracks[i];
            double y = timeAxisHeight + (i - startTrack) * trackHeight;

            Color rowBg = i % 2 == 0 ? Color.FromRgb(0x22, 0x22, 0x22) : Color.FromRgb(0x2A, 0x2A, 0x2A);
            dc.DrawRectangle(new SolidColorBrush(rowBg), null, new Rect(0, y, width, trackHeight));

            DrawClip(dc, track, y, trackHeight, scrollSeconds, visibleSeconds, timelineWidth, dpiScaleX, timeAxisHeight);
        }

        DrawTimeAxis(dc, timelineWidth, scrollSeconds, visibleSeconds, dpiScaleX, timeAxisHeight);
    }

    private void DrawClip(DrawingContext dc, PlaylistTrack track, double y, double trackHeight, double scrollSeconds, double visibleSeconds, double timelineWidth, double dpiScaleX, double timeAxisHeight)
    {
        double clipStart = track.GetActualTimelineIn().TotalSeconds;
        double clipDuration = track.GetEffectiveDuration().TotalSeconds;
        double clipEnd = clipStart + clipDuration;

        if (clipEnd < scrollSeconds || clipStart > scrollSeconds + visibleSeconds)
            return;

        double x = (clipStart - scrollSeconds) / _displayState.SecondsPerPixel * dpiScaleX;
        double width = clipDuration / _displayState.SecondsPerPixel * dpiScaleX;

        if (track.MediaDuration == TimeSpan.Zero)
        {
            width = Math.Max(MinClipWidthPx * dpiScaleX, width);
        }

        int trackIndex = _playlist.FindIndexById(track.Id);
        Color clipColor = GetTrackColor(trackIndex);
        double opacity = track.IsEnabled ? 1.0 : 0.5;

        var brush = new SolidColorBrush(clipColor) { Opacity = opacity };
        dc.DrawRectangle(brush, null, new Rect(x, y, width, trackHeight));

        if (width > TextWidthThresholdPx)
        {
            var formattedText = new FormattedText(
                track.Name,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            formattedText.MaxTextWidth = width - 4;
            formattedText.Trimming = TextTrimming.CharacterEllipsis;

            dc.DrawText(formattedText, new Point(x + 2, y + (trackHeight - formattedText.Height) / 2));
        }
    }

    private void DrawTimeAxis(DrawingContext dc, double timelineWidth, double scrollSeconds, double visibleSeconds, double dpiScaleX, double timeAxisHeight)
    {
        double interval = GetTimeAxisInterval();
        double start = Math.Floor(scrollSeconds / interval) * interval;
        double end = scrollSeconds + visibleSeconds;

        for (double t = start; t <= end; t += interval)
        {
            double x = (t - scrollSeconds) / _displayState.SecondsPerPixel * dpiScaleX;
            dc.DrawLine(new Pen(Brushes.Gray, 1), new Point(x, 0), new Point(x, timeAxisHeight));

            string label = FormatTimeLabel(t);
            var formattedText = new FormattedText(
                label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10,
                Brushes.Gray,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(formattedText, new Point(x + 2, 2));
        }
    }

    private void RenderPlayhead()
    {
        using DrawingContext dc = _playheadVisual.RenderOpen();

        if (!_displayState.IsVisible || _playlist.Tracks.Count == 0)
            return;

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double dpiScaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double dpiScaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        double timeAxisHeight = TimeAxisHeightPx * dpiScaleY;

        double x = (_playbackPositionSeconds - _displayState.HorizontalScrollSeconds) / _displayState.SecondsPerPixel * dpiScaleX;

        var pen = new Pen(Brushes.Red, 2);
        dc.DrawLine(pen, new Point(x, timeAxisHeight), new Point(x, height));
    }

    private double GetTimeAxisInterval()
    {
        double secondsPerPixel = _displayState.SecondsPerPixel;

        // 目盛間のピクセル距離が50-150pxになるように間隔を計算
        double[] intervals = { 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 };

        foreach (double interval in intervals)
        {
            double pixelDistance = interval / secondsPerPixel;
            if (pixelDistance >= TickIntervalTargetPx)
                return interval;
        }

        return intervals[^1];
    }

    internal static string FormatTimeLabel(double seconds)
    {
        if (seconds >= 3600)
        {
            return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");
        }
        return TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
    }

    internal static Color GetTrackColor(int trackIndex)
    {
        return trackIndex switch
        {
            0 => Color.FromRgb(0x3B, 0x82, 0xF6),
            1 => Color.FromRgb(0x10, 0xB9, 0x81),
            2 => Color.FromRgb(0x8B, 0x5C, 0xF6),
            _ => HslToColor((trackIndex * 30) % 360, 50, 50)
        };
    }

    internal static Color HslToColor(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l / 100.0 - 1)) * s / 100.0;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l / 100.0 - c / 2;

        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }

        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    public void UpdatePlaybackPosition(double seconds)
    {
        _playbackPositionSeconds = seconds;

        double width = ActualWidth;
        if (width <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double dpiScaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;

        double x = (seconds - _displayState.HorizontalScrollSeconds) / _displayState.SecondsPerPixel * dpiScaleX;

        if (x > width - 20 || x < 20)
        {
            _displayState.HorizontalScrollSeconds = seconds - width * _displayState.SecondsPerPixel / 2;
            RenderClips();
        }

        if (_loadedTrackId.HasValue)
        {
            var cooldown = DateTime.Now - _lastVerticalScrollAt;
            if (cooldown.TotalMilliseconds >= VerticalFollowCooldownMs)
            {
                int trackIndex = _playlist.FindIndexById(_loadedTrackId.Value);
                if (trackIndex >= 0)
                {
                    double height = ActualHeight;
                    var dpiY = VisualTreeHelper.GetDpi(this);
                    double dpiScaleY = dpiY.DpiScaleY > 0 ? dpiY.DpiScaleY : 1.0;
                    double timeAxisHeight = TimeAxisHeightPx * dpiScaleY;
                    double trackHeight = _displayState.TrackHeight;
                    double visibleTracks = (height - timeAxisHeight) / trackHeight;
                    int startTrack = _displayState.VerticalScrollOffset;
                    int endTrack = startTrack + (int)visibleTracks;

                    if (trackIndex < startTrack || trackIndex >= endTrack)
                    {
                        _displayState.VerticalScrollOffset = Math.Max(0, trackIndex - (int)visibleTracks / 2);
                        RenderClips();
                    }
                }
            }
        }

        RenderPlayhead();
    }

    private void TimelineDrawingSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double dpiScaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double dpiScaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        double timeAxisHeight = TimeAxisHeightPx * dpiScaleY;

        var point = e.GetPosition(this);
        double x = point.X;
        double y = point.Y - timeAxisHeight;

        if (x < 0 || y < 0)
            return;

        int trackIndex = _displayState.VerticalScrollOffset + (int)(y / _displayState.TrackHeight);
        if (trackIndex < 0 || trackIndex >= _playlist.Tracks.Count)
            return;

        var track = _playlist.Tracks[trackIndex];
        double clickSeconds = _displayState.HorizontalScrollSeconds + x / dpiScaleX * _displayState.SecondsPerPixel;

        double targetSeconds = track.GetActualTimelineIn().TotalSeconds +
            (clickSeconds - track.GetActualTimelineIn().TotalSeconds);

        TimelineSeekRequested?.Invoke(this, new TimelineSeekEventArgs(targetSeconds, trackIndex));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _playlist.Tracks.CollectionChanged -= OnTracksCollectionChanged;
        MouseLeftButtonUp -= TimelineDrawingSurface_MouseLeftButtonUp;
        TimelineSeekRequested = null;
    }
}
