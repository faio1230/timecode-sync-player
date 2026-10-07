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
    private const double TextWidthThresholdPx = 30;

    internal event EventHandler<TimelineSeekEventArgs>? TimelineSeekRequested;

    /// <summary>v0.6.6 F-2: ドラッグ中の目標（押した行に固定）。離したときは <see cref="TimelineSeekRequested"/>。</summary>
    internal event EventHandler<TimelineScrubEventArgs>? TimelineScrubMoved;

    private readonly TimelineScrubGesture _scrubGesture = new();
    private double _lastScrubX;
    private bool _releasingCapture;

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
        MouseLeftButtonDown += TimelineDrawingSurface_MouseLeftButtonDown;
        MouseMove += TimelineDrawingSurface_MouseMove;
        MouseLeftButtonUp += TimelineDrawingSurface_MouseLeftButtonUp;
        LostMouseCapture += TimelineDrawingSurface_LostMouseCapture;

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

        double pivotSeconds = TimelineLayoutCalculator.CalculateZoomPivot(
            _playbackPositionSeconds,
            _displayState.HorizontalScrollSeconds,
            _displayState.VisibleSeconds(width));
        _displayState.ZoomIn(pivotSeconds);
        RenderClips();
    }

    public void ZoomOut()
    {
        double width = ActualWidth;
        if (width <= 0) return;

        double pivotSeconds = TimelineLayoutCalculator.CalculateZoomPivot(
            _playbackPositionSeconds,
            _displayState.HorizontalScrollSeconds,
            _displayState.VisibleSeconds(width));
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

        TimelineViewportLayout layout = TimelineLayoutCalculator.CalculateViewport(
            width,
            height,
            dpiScaleY,
            _displayState.SecondsPerPixel,
            _displayState.HorizontalScrollSeconds,
            _displayState.TrackHeight,
            _displayState.VerticalScrollOffset,
            _playlist.Tracks.Count);

        for (int i = layout.StartTrack; i < layout.EndTrack; i++)
        {
            var track = _playlist.Tracks[i];
            double y = TimelineLayoutCalculator.CalculateTrackY(
                i,
                layout.StartTrack,
                layout.TimeAxisHeight,
                layout.TrackHeight);

            Color rowBg = i % 2 == 0 ? Color.FromRgb(0x22, 0x22, 0x22) : Color.FromRgb(0x2A, 0x2A, 0x2A);
            dc.DrawRectangle(new SolidColorBrush(rowBg), null, new Rect(0, y, width, layout.TrackHeight));

            DrawClip(
                dc,
                track,
                y,
                layout.TrackHeight,
                layout.ScrollSeconds,
                layout.VisibleSeconds,
                dpiScaleX);
        }

        DrawTimeAxis(
            dc,
            layout.ScrollSeconds,
            layout.VisibleSeconds,
            dpiScaleX,
            layout.TimeAxisHeight);
    }

    private void DrawClip(DrawingContext dc, PlaylistTrack track, double y, double trackHeight, double scrollSeconds, double visibleSeconds, double dpiScaleX)
    {
        double clipStart = track.GetActualTimelineIn().TotalSeconds;
        double clipDuration = track.GetEffectiveDuration().TotalSeconds;
        TimelineClipLayout? layout = TimelineLayoutCalculator.CalculateClip(
            clipStart,
            clipDuration,
            track.MediaDuration != TimeSpan.Zero,
            scrollSeconds,
            visibleSeconds,
            _displayState.SecondsPerPixel,
            dpiScaleX);
        if (layout == null)
            return;

        double x = layout.Value.X;
        double width = layout.Value.Width;

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

    private void DrawTimeAxis(DrawingContext dc, double scrollSeconds, double visibleSeconds, double dpiScaleX, double timeAxisHeight)
    {
        IReadOnlyList<TimelineTimeTick> ticks = TimelineLayoutCalculator.CalculateTimeTicks(
            scrollSeconds,
            visibleSeconds,
            _displayState.SecondsPerPixel,
            dpiScaleX);
        foreach (TimelineTimeTick tick in ticks)
        {
            dc.DrawLine(new Pen(Brushes.Gray, 1), new Point(tick.X, 0), new Point(tick.X, timeAxisHeight));

            string label = FormatTimeLabel(tick.Seconds);
            var formattedText = new FormattedText(
                label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10,
                Brushes.Gray,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            dc.DrawText(formattedText, new Point(tick.X + 2, 2));
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
        double timeAxisHeight = TimelineLayoutCalculator.TimeAxisHeightPx * dpiScaleY;

        double x = TimelineLayoutCalculator.SecondsToX(
            _playbackPositionSeconds,
            _displayState.HorizontalScrollSeconds,
            _displayState.SecondsPerPixel,
            dpiScaleX);

        var pen = new Pen(Brushes.Red, 2);
        dc.DrawLine(pen, new Point(x, timeAxisHeight), new Point(x, height));
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

        double? horizontalScroll = TimelineLayoutCalculator.CalculateHorizontalFollowScroll(
            seconds,
            _displayState.HorizontalScrollSeconds,
            _displayState.SecondsPerPixel,
            dpiScaleX,
            width);

        if (horizontalScroll.HasValue)
        {
            _displayState.HorizontalScrollSeconds = horizontalScroll.Value;
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
                    double trackHeight = _displayState.TrackHeight;
                    int startTrack = _displayState.VerticalScrollOffset;
                    int? verticalScrollOffset = TimelineLayoutCalculator.CalculateVerticalFollowOffset(
                        trackIndex,
                        height,
                        dpiScaleY,
                        trackHeight,
                        startTrack);

                    if (verticalScrollOffset.HasValue)
                    {
                        _displayState.VerticalScrollOffset = verticalScrollOffset.Value;
                        RenderClips();
                    }
                }
            }
        }

        RenderPlayhead();
    }

    // v0.6.6 F-2: 押した行に固定してドラッグする（行の扱いは TimelineScrubGesture）。
    private void TimelineDrawingSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var point = e.GetPosition(this);
        int? trackIndex = HitTrackIndex(point);
        _scrubGesture.Press(trackIndex, point.X);
        _lastScrubX = point.X;
        if (trackIndex.HasValue)
            CaptureMouse();
    }

    private void TimelineDrawingSurface_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _scrubGesture.PinnedTrackIndex is null) return;
        double x = e.GetPosition(this).X;
        TimelineScrubMove move = _scrubGesture.Move(x, SystemParameters.MinimumHorizontalDragDistance);
        if (!move.Moved) return;
        _lastScrubX = x;
        if (!TryCalculateTarget(x, move.TrackIndex, out double targetSeconds)) return;
        TimelineScrubMoved?.Invoke(this, new TimelineScrubEventArgs(targetSeconds, move.TrackIndex, move.Started));
    }

    private void TimelineDrawingSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            _scrubGesture.Cancel();
            ReleaseScrubCapture();
            return;
        }

        var point = e.GetPosition(this);
        int? clickedTrackIndex = HitTrackIndex(point);
        TimelineScrubRelease release = _scrubGesture.Release(clickedTrackIndex);
        ReleaseScrubCapture();
        RaiseRelease(release, point.X);
    }

    private void TimelineDrawingSurface_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_releasingCapture || !_scrubGesture.IsDragging) return;
        // 捕捉を失った（別の窓へ移った等）: 押した行のまま、最後の位置でドラッグを締める。
        TimelineScrubRelease release = _scrubGesture.Release(null);
        RaiseRelease(release, _lastScrubX);
    }

    private void RaiseRelease(TimelineScrubRelease release, double x)
    {
        // 行の外で離したただのクリックは今のまま何もしない。
        if (release.TrackIndex is not int trackIndex) return;
        if (!TryCalculateTarget(x, trackIndex, out double targetSeconds)) return;
        TimelineSeekRequested?.Invoke(this, new TimelineSeekEventArgs(targetSeconds, trackIndex, release.EndsDrag));
    }

    private void ReleaseScrubCapture()
    {
        if (!IsMouseCaptured) return;
        _releasingCapture = true;
        try { ReleaseMouseCapture(); }
        finally { _releasingCapture = false; }
    }

    private int? HitTrackIndex(Point point)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double dpiScaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;
        return TimelineLayoutCalculator.CalculateClickedTrackIndex(
            point.X,
            point.Y,
            dpiScaleY,
            _displayState.TrackHeight,
            _displayState.VerticalScrollOffset,
            _playlist.Tracks.Count);
    }

    private bool TryCalculateTarget(double x, int trackIndex, out double targetSeconds)
    {
        targetSeconds = 0;
        if (trackIndex < 0 || trackIndex >= _playlist.Tracks.Count) return false;
        var dpi = VisualTreeHelper.GetDpi(this);
        double dpiScaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        // ドラッグ中は捕捉しているので、面の外（左端より左・右端より右）の位置も来る。面の幅に収める。
        double clampedX = Math.Clamp(x, 0, Math.Max(0, ActualWidth));
        var track = _playlist.Tracks[trackIndex];
        targetSeconds = TimelineLayoutCalculator.CalculateSeekTargetSeconds(
            clampedX,
            dpiScaleX,
            _displayState.SecondsPerPixel,
            _displayState.HorizontalScrollSeconds,
            track.GetActualTimelineIn().TotalSeconds);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _playlist.Tracks.CollectionChanged -= OnTracksCollectionChanged;
        MouseLeftButtonDown -= TimelineDrawingSurface_MouseLeftButtonDown;
        MouseMove -= TimelineDrawingSurface_MouseMove;
        MouseLeftButtonUp -= TimelineDrawingSurface_MouseLeftButtonUp;
        LostMouseCapture -= TimelineDrawingSurface_LostMouseCapture;
        TimelineSeekRequested = null;
        TimelineScrubMoved = null;
    }
}
