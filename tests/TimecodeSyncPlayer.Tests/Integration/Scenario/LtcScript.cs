namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 C3: LTC 入力の台本（設計: docs/design/v0.5.4-scenario-layer.md §2-3）。
/// Normal／Duplicate／Jump／無音／Raw を時刻つきで並べ、<see cref="AdvanceTime"/> で
/// 予定時刻に発行する。時刻は台本の内部時計（ミリ秒）で、開始は harness が渡す単調ミリ秒。
/// </summary>
internal sealed class LtcScript
{
    private readonly record struct ScheduledFrame(long AtMilliseconds, LtcFrameProcessingResult Frame);

    private readonly Action<LtcFrameProcessingResult, long> _emit;
    private readonly List<ScheduledFrame> _frames = [];
    private readonly double _defaultFps;
    private long _nowMilliseconds;
    private long _cursorMilliseconds;
    private int _next;

    public LtcScript(
        Action<LtcFrameProcessingResult, long> emit,
        long startMilliseconds = 0,
        double defaultFps = 25.0)
    {
        _emit = emit;
        _nowMilliseconds = startMilliseconds;
        _cursorMilliseconds = startMilliseconds;
        _defaultFps = defaultFps;
    }

    /// <summary>台本の現在時刻（ミリ秒）。</summary>
    public long NowMilliseconds => _nowMilliseconds;

    /// <summary>次に足す区間が始まる時刻（ミリ秒）。</summary>
    public long NextMilliseconds => _cursorMilliseconds;

    public int ScheduledFrameCount => _frames.Count;
    public int EmittedFrameCount => _next;
    public bool IsFinished => _next >= _frames.Count;

    /// <summary>進み続ける Normal を duration の間、グリッド（既定 25fps）で並べる。</summary>
    public LtcScript Normal(double fromSeconds, TimeSpan duration, double? fps = null, long? atMilliseconds = null)
    {
        long start = Begin(duration, atMilliseconds);
        double stepFps = fps ?? _defaultFps;
        long durationMs = (long)duration.TotalMilliseconds;
        for (int i = 0; FrameOffsetMilliseconds(i, stepFps) < durationMs; i++)
        {
            AddFrame(start + FrameOffsetMilliseconds(i, stepFps), TimecodeFrameDiagnosticStatus.Normal,
                fromSeconds + i / stepFps, stepFps, shouldApplySync: true);
        }
        return this;
    }

    /// <summary>値が進まない保持（Duplicate）を並べる。損失の確定は時計の進みに任せる。</summary>
    public LtcScript Duplicate(double seconds, TimeSpan duration, double? fps = null, long? atMilliseconds = null)
    {
        long start = Begin(duration, atMilliseconds);
        double stepFps = fps ?? _defaultFps;
        long durationMs = (long)duration.TotalMilliseconds;
        for (int i = 0; FrameOffsetMilliseconds(i, stepFps) < durationMs; i++)
        {
            AddFrame(start + FrameOffsetMilliseconds(i, stepFps), TimecodeFrameDiagnosticStatus.Duplicate,
                seconds, stepFps, shouldApplySync: false);
        }
        return this;
    }

    /// <summary>値が動いた Jump を count 枚（既定 1 枚。D27-b の即時復帰用）。</summary>
    public LtcScript Jump(double toSeconds, int count = 1, long? atMilliseconds = null)
    {
        long durationMs = count * FrameOffsetMilliseconds(1, _defaultFps);
        long start = Begin(TimeSpan.FromMilliseconds(durationMs), atMilliseconds);
        for (int i = 0; i < count; i++)
        {
            AddFrame(start + FrameOffsetMilliseconds(i, _defaultFps), TimecodeFrameDiagnosticStatus.Jump,
                toSeconds, _defaultFps, shouldApplySync: false);
        }
        return this;
    }

    /// <summary>無音。フレームを出さず、時刻だけ進める。</summary>
    public LtcScript Silence(TimeSpan duration, long? atMilliseconds = null)
    {
        Begin(duration, atMilliseconds);
        return this;
    }

    /// <summary>状態と同期適用の可否を明示したフレームを並べる（細かい再現用）。</summary>
    public LtcScript Raw(
        TimecodeFrameDiagnosticStatus status, double seconds, int count = 1,
        bool shouldApplySync = false, long? atMilliseconds = null)
    {
        long durationMs = count * FrameOffsetMilliseconds(1, _defaultFps);
        long start = Begin(TimeSpan.FromMilliseconds(durationMs), atMilliseconds);
        for (int i = 0; i < count; i++)
        {
            AddFrame(start + FrameOffsetMilliseconds(i, _defaultFps), status, seconds, _defaultFps,
                shouldApplySync);
        }
        return this;
    }

    /// <summary>時計の進みに合わせて、予定時刻を過ぎたフレームを予定時刻つきで発行する。</summary>
    public void AdvanceTime(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delta), "LtcScript は戻らない");

        long target = _nowMilliseconds + (long)delta.TotalMilliseconds;
        _nowMilliseconds = target;
        while (_next < _frames.Count && _frames[_next].AtMilliseconds <= target)
        {
            ScheduledFrame scheduled = _frames[_next];
            _next++;
            _emit(scheduled.Frame, scheduled.AtMilliseconds);
        }
    }

    private long Begin(TimeSpan duration, long? atMilliseconds)
    {
        long start = atMilliseconds ?? _cursorMilliseconds;
        long end = start + (long)duration.TotalMilliseconds;
        if (end > _cursorMilliseconds)
            _cursorMilliseconds = end;
        return start;
    }

    private void AddFrame(
        long atMilliseconds, TimecodeFrameDiagnosticStatus status, double seconds, double fps,
        bool shouldApplySync)
    {
        _frames.Add(new ScheduledFrame(atMilliseconds, new LtcFrameProcessingResult(
            "scenario", $"{seconds:F3} s", seconds, fps, $"fps: {fps:0}",
            new TimecodeFrameDiagnosticResult(status, 0, 0),
            ShouldApplySync: shouldApplySync, ShouldLogFps: false)));
    }

    private static long FrameOffsetMilliseconds(int index, double fps) =>
        (long)Math.Round(index * 1000.0 / fps);
}
