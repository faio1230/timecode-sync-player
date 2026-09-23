namespace TimecodeSyncPlayer;

public sealed class PlaybackPerformanceStats
{
    private readonly TimeSpan _window;
    private TimeSpan? _windowStartedAt;
    private double _firstPlaybackSeconds;
    private double _lastPlaybackSeconds;
    // 0.4.8: 位置が戻った区間の手前までに進んだ量。窓は作り直さず、戻りは回数と最大幅で数える。
    private double _foldedAdvanceSeconds;
    private int _backwardJumps;
    private double _maxBackwardSeconds;
    // 0.4.8 候補 2: 窓を始めた時点の操作の回数（シーク・読み込み・一時停止など）。
    private long _windowOperationEpoch;
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

    /// <summary>テスト用: 壁時計の値をそのまま単調な経過時間として扱う。</summary>
    public PlaybackPerformanceSnapshot? RecordTick(double playbackSeconds, DateTime now, long operationEpoch = 0)
        => RecordTick(playbackSeconds, TimeSpan.FromTicks(now.Ticks), operationEpoch);

    /// <summary>
    /// <paramref name="monotonicNow"/> は単調時計（Stopwatch 由来）の経過時間。壁時計の補正で窓が伸び縮みしない。
    /// 0.4.8: 操作が無いのに位置が戻ったとき（照会値の揺れ）は窓を作り直さない。戻る直前までの進みを
    /// 畳んで持ち越し、戻りは <see cref="PlaybackPerformanceSnapshot.BackwardJumps"/> として別に数える
    /// （以前は窓が黙って作り直され、揺れが続くと性能ログが 20 秒以上途切れた）。
    /// <paramref name="operationEpoch"/>（シーク・読み込み・一時停止などの回数）が窓の開始から変わって
    /// いれば、位置の向きに関係なく窓を作り直す（操作をまたいだ窓は意味を持たない）。
    /// </summary>
    public PlaybackPerformanceSnapshot? RecordTick(double playbackSeconds, TimeSpan monotonicNow, long operationEpoch = 0)
    {
        if (!double.IsFinite(playbackSeconds) || playbackSeconds < 0)
            return null;

        TimeSpan now = monotonicNow;
        if (_windowStartedAt == null)
        {
            StartWindow(playbackSeconds, now, operationEpoch);
            return null;
        }

        // 操作（シーク・読み込み・一時停止など）をまたいだ窓は意味を持たないので、向きに関係なく作り直す。
        // 0.4.8 候補 2 は「戻ったときだけ」作り直していたため、読み込みと先頭の暗転をまたいで位置が前へ
        // 進む形（HAP の L-2）で、6.2 秒・1.6 倍の窓ができた。
        if (operationEpoch != _windowOperationEpoch)
        {
            StartWindow(playbackSeconds, now, operationEpoch);
            return null;
        }

        if (playbackSeconds < _lastPlaybackSeconds)
        {
            _foldedAdvanceSeconds += _lastPlaybackSeconds - _firstPlaybackSeconds;
            _backwardJumps++;
            _maxBackwardSeconds = Math.Max(_maxBackwardSeconds, _lastPlaybackSeconds - playbackSeconds);
            _firstPlaybackSeconds = playbackSeconds;
        }

        _tickCount++;
        _lastPlaybackSeconds = playbackSeconds;

        TimeSpan elapsed = now - _windowStartedAt.Value;
        if (elapsed < _window)
            return null;

        PlaybackPerformanceSnapshot snapshot = CreateSnapshot(elapsed);
        StartWindow(playbackSeconds, now, operationEpoch);
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

    /// <summary>
    /// 0.4.7: 窓が新しく始まるたびに 1 増える。窓ごとの基準を取り直したい側（<see cref="DecodeHealthMonitor"/>）が使う。
    /// 0.4.8: 窓が始まるのは最初の tick・snapshot を返したとき・<see cref="Reset"/> の後と、
    /// 操作（シークなど）の後の最初の tick（操作の無い位置の戻りでは始まらない）。
    /// </summary>
    public long WindowGeneration { get; private set; }

    private void StartWindow(double playbackSeconds, TimeSpan now, long operationEpoch)
    {
        WindowGeneration++;
        _windowOperationEpoch = operationEpoch;
        _windowStartedAt = now;
        _firstPlaybackSeconds = playbackSeconds;
        _lastPlaybackSeconds = playbackSeconds;
        _foldedAdvanceSeconds = 0;
        _backwardJumps = 0;
        _maxBackwardSeconds = 0;
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
            ? (_foldedAdvanceSeconds + _lastPlaybackSeconds - _firstPlaybackSeconds) / elapsedSeconds
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
            _spoutEnabled,
            _backwardJumps,
            _maxBackwardSeconds);
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
    bool SpoutEnabled,
    int BackwardJumps = 0,
    double MaxBackwardSeconds = 0);
