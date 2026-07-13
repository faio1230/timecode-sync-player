namespace TimecodeSyncPlayer;

public sealed class PlaybackPerformanceStats
{
    private readonly TimeSpan _window;
    private DateTime? _windowStartedAt;
    private double _firstPlaybackSeconds;
    private double _lastPlaybackSeconds;
    private int _tickCount;
    private int _renderUpdates;
    private int _frameUpdates;
    private int _renderedFrames;
    private double _renderMsTotal;
    private double _bitmapMsTotal;
    private double _spoutMsTotal;
    private double _renderMsMax;
    private double _bitmapMsMax;
    private double _spoutMsMax;
    private int _width;
    private int _height;
    private bool _spoutEnabled;
    private long _totalRenderedFrames;

    public PlaybackPerformanceStats(TimeSpan window)
    {
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window));

        _window = window;
    }

    public PlaybackPerformanceSnapshot? RecordTick(double playbackSeconds, DateTime now)
    {
        if (!double.IsFinite(playbackSeconds) || playbackSeconds < 0)
            return null;

        if (_windowStartedAt == null)
        {
            StartWindow(playbackSeconds, now);
            return null;
        }

        if (playbackSeconds < _lastPlaybackSeconds)
        {
            StartWindow(playbackSeconds, now);
            return null;
        }

        _tickCount++;
        _lastPlaybackSeconds = playbackSeconds;

        TimeSpan elapsed = now - _windowStartedAt.Value;
        if (elapsed < _window)
            return null;

        PlaybackPerformanceSnapshot snapshot = CreateSnapshot(elapsed);
        StartWindow(playbackSeconds, now);
        return snapshot;
    }

    public void RecordRenderUpdate(bool hasFrame)
    {
        if (_windowStartedAt == null)
            return;

        _renderUpdates++;
        if (hasFrame)
            _frameUpdates++;
    }

    public void RecordRenderedFrame(
        double renderMs,
        double bitmapMs,
        double spoutMs,
        int width,
        int height,
        bool spoutEnabled)
    {
        if (_windowStartedAt == null)
            return;

        _renderedFrames++;
        _totalRenderedFrames++;
        _renderMsTotal += renderMs;
        _bitmapMsTotal += bitmapMs;
        _spoutMsTotal += spoutMs;
        _renderMsMax = Math.Max(_renderMsMax, renderMs);
        _bitmapMsMax = Math.Max(_bitmapMsMax, bitmapMs);
        _spoutMsMax = Math.Max(_spoutMsMax, spoutMs);
        _width = width;
        _height = height;
        _spoutEnabled = spoutEnabled;
    }

    public void Reset()
    {
        _windowStartedAt = null;
        _totalRenderedFrames = 0;
        ClearWindowCounters();
    }

    public long TotalRenderedFrames => _totalRenderedFrames;

    private void StartWindow(double playbackSeconds, DateTime now)
    {
        _windowStartedAt = now;
        _firstPlaybackSeconds = playbackSeconds;
        _lastPlaybackSeconds = playbackSeconds;
        _tickCount = 1;
        ClearWindowCounters();
    }

    private void ClearWindowCounters()
    {
        _renderUpdates = 0;
        _frameUpdates = 0;
        _renderedFrames = 0;
        _renderMsTotal = 0;
        _bitmapMsTotal = 0;
        _spoutMsTotal = 0;
        _renderMsMax = 0;
        _bitmapMsMax = 0;
        _spoutMsMax = 0;
        _width = 0;
        _height = 0;
        _spoutEnabled = false;
    }

    private PlaybackPerformanceSnapshot CreateSnapshot(TimeSpan elapsed)
    {
        double elapsedSeconds = elapsed.TotalSeconds;
        double playbackRate = elapsedSeconds > 0
            ? (_lastPlaybackSeconds - _firstPlaybackSeconds) / elapsedSeconds
            : 0;
        double displayedFps = elapsedSeconds > 0 ? _renderedFrames / elapsedSeconds : 0;
        double avgRenderMs = _renderedFrames > 0 ? _renderMsTotal / _renderedFrames : 0;
        double avgBitmapMs = _renderedFrames > 0 ? _bitmapMsTotal / _renderedFrames : 0;
        double avgSpoutMs = _renderedFrames > 0 ? _spoutMsTotal / _renderedFrames : 0;

        return new PlaybackPerformanceSnapshot(
            elapsed,
            _tickCount,
            _renderUpdates,
            _frameUpdates,
            _renderedFrames,
            playbackRate,
            displayedFps,
            avgRenderMs,
            avgBitmapMs,
            avgSpoutMs,
            _renderMsMax,
            _bitmapMsMax,
            _spoutMsMax,
            _width,
            _height,
            _spoutEnabled);
    }
}

public sealed record PlaybackPerformanceSnapshot(
    TimeSpan Elapsed,
    int TickCount,
    int RenderUpdates,
    int FrameUpdates,
    int RenderedFrames,
    double PlaybackRate,
    double DisplayedFps,
    double AvgRenderMs,
    double AvgBitmapMs,
    double AvgSpoutMs,
    double MaxRenderMs,
    double MaxBitmapMs,
    double MaxSpoutMs,
    int Width,
    int Height,
    bool SpoutEnabled);
