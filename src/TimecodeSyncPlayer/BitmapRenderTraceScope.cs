namespace TimecodeSyncPlayer;

/// <summary>
/// Synchronous, one-use attribution for the display call inside PublishTraced.
/// Never flows through await or to another thread. Nested publications restore
/// their parent; direct renderer reentry cannot reuse an already consumed scope.
/// </summary>
internal sealed class BitmapRenderTraceScope : IDisposable
{
    [ThreadStatic] private static BitmapRenderTraceScope? _current;
    private readonly BitmapRenderTraceScope? _previous;
    private readonly SyncAccuracyTrace _trace;
    private readonly long _sessionId, _sequence;
    private readonly int _generation, _width, _height;
    private bool _consumed;

    public BitmapRenderTraceScope(SyncAccuracyTrace trace, long sessionId, int generation,
        long sequence, int width, int height)
    {
        _trace = trace; _sessionId = sessionId; _generation = generation;
        _sequence = sequence; _width = width; _height = height;
        _previous = _current;
        _current = this;
    }

    public static BitmapRenderTraceScope? Take(SyncAccuracyTrace trace, int width, int height)
    {
        var scope = _current;
        if (scope == null || scope._consumed || !ReferenceEquals(scope._trace, trace) ||
            scope._width != width || scope._height != height) return null;
        scope._consumed = true;
        return scope;
    }

    public void Record(string stage, bool completed, long start, long end)
        => Record(stage, completed ? "completed" : "exception", start, end);

    public void Record(string stage, string outcome, long start, long end)
        => _trace.RecordRenderStage(_sessionId, null, _generation, _sequence, stage,
            outcome, start, end, _width, _height);

    public void Dispose() => _current = _previous;
}
