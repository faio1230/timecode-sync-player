using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TimecodeSyncPlayer;

public partial class TimelinePanel : UserControl, IDisposable
{
    private bool _disposed;
    private readonly PlaylistState _playlist;
    private TimelineDrawingSurface? _drawingSurface;

    internal event EventHandler<TimelineSeekEventArgs>? TimelineSeekRequested;

    public Guid? LoadedTrackId
    {
        get => _drawingSurface?.LoadedTrackId;
        set
        {
            if (_drawingSurface != null)
                _drawingSurface.LoadedTrackId = value;
        }
    }

    public bool IsTimelineVisible
    {
        get => _drawingSurface?.IsTimelineVisible ?? false;
        set
        {
            if (_drawingSurface != null)
                _drawingSurface.IsTimelineVisible = value;
        }
    }

    public TimelinePanel(PlaylistState playlist, bool isTimelineVisible)
    {
        _playlist = playlist;
        InitializeComponent();

        _drawingSurface = new TimelineDrawingSurface(playlist, isTimelineVisible);
        _drawingSurface.TimelineSeekRequested += DrawingSurface_TimelineSeekRequested;
        _drawingSurface.SizeChanged += DrawingSurface_SizeChanged;

        DrawingSurfaceContainer.Child = _drawingSurface;

        UpdateScrollBarRanges();
        _playlist.Tracks.CollectionChanged += OnTracksCollectionChanged;
    }

    private void OnTracksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateScrollBarRanges();

    private void UpdateScrollBarRanges()
    {
        if (_drawingSurface == null) return;

        var state = _drawingSurface.DisplayState;
        double width = _drawingSurface.ActualWidth;
        double height = _drawingSurface.ActualHeight;

        if (width > 0)
        {
            double visibleSeconds = state.VisibleSeconds(width);
            double totalSeconds = GetTotalTimelineSeconds();
            HorizontalScrollBar.Maximum = Math.Max(0, totalSeconds - visibleSeconds);
            HorizontalScrollBar.ViewportSize = visibleSeconds;
            HorizontalScrollBar.Value = state.HorizontalScrollSeconds;
        }

        if (height > 0)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double trackHeight = state.TrackHeight * (dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0);
            double timeAxisHeight = 20 * (dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0);
            double visibleTracks = (height - timeAxisHeight) / trackHeight;
            int totalTracks = _playlist.Tracks.Count;
            VerticalScrollBar.Maximum = Math.Max(0, totalTracks - visibleTracks);
            VerticalScrollBar.ViewportSize = visibleTracks;
            VerticalScrollBar.Value = state.VerticalScrollOffset;
        }

        UpdateZoomLevelText();
    }

    private double GetTotalTimelineSeconds()
    {
        if (_playlist.Tracks.Count == 0) return 0;
        double max = 0;
        foreach (var track in _playlist.Tracks)
        {
            double end = track.GetActualTimelineOut().TotalSeconds;
            if (end > max) max = end;
        }
        return max;
    }

    private void UpdateZoomLevelText()
    {
        if (_drawingSurface == null) return;

        double secondsPerPixel = _drawingSurface.DisplayState.SecondsPerPixel;
        double baseSecondsPerPixel = 1.0;
        double zoomPercent = (baseSecondsPerPixel / secondsPerPixel) * 100;
        ZoomLevelText.Text = $"{zoomPercent:F0}%";
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _drawingSurface?.ZoomIn();
        UpdateScrollBarRanges();
    }

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _drawingSurface?.ZoomOut();
        UpdateScrollBarRanges();
    }

    private void HorizontalScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        if (_drawingSurface == null) return;

        double delta = e.NewValue - _drawingSurface.DisplayState.HorizontalScrollSeconds;
        _drawingSurface.ScrollHorizontal(delta);
        UpdateScrollBarRanges();
    }

    private void VerticalScrollBar_Scroll(object sender, ScrollEventArgs e)
    {
        if (_drawingSurface == null) return;

        int delta = (int)e.NewValue - _drawingSurface.DisplayState.VerticalScrollOffset;
        _drawingSurface.ScrollVertical(delta);
        UpdateScrollBarRanges();
    }

    private void DrawingSurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateScrollBarRanges();
    }

    private void DrawingSurface_TimelineSeekRequested(object? sender, TimelineSeekEventArgs e)
    {
        TimelineSeekRequested?.Invoke(this, e);
    }

    public void UpdatePlaybackPosition(double seconds)
    {
        _drawingSurface?.UpdatePlaybackPosition(seconds);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_drawingSurface != null)
        {
            _drawingSurface.TimelineSeekRequested -= DrawingSurface_TimelineSeekRequested;
            _drawingSurface.SizeChanged -= DrawingSurface_SizeChanged;
        }

        _playlist.Tracks.CollectionChanged -= OnTracksCollectionChanged;

        _drawingSurface?.Dispose();
        _drawingSurface = null;
    }
}
