using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

public sealed class LtcFrameProcessor
{
    private readonly ITimecodeFpsSelector _fpsSelector;
    private readonly TimecodeFrameDiagnostics _diagnostics;
    private double _lastTimecodeFps;
    private double _lastLoggedTimecodeFps;

    public LtcFrameProcessor(ITimecodeFpsSelector fpsSelector, TimecodeFrameDiagnostics diagnostics)
    {
        _fpsSelector = fpsSelector;
        _diagnostics = diagnostics;
    }

    public double LastTimecodeFps => _lastTimecodeFps;

    public void ResetDiagnostics()
    {
        _diagnostics.Reset();
    }

    public void ResetForFpsMode(TimecodeFpsMode mode)
    {
        _fpsSelector.Reset();
        _diagnostics.Reset();
        _lastTimecodeFps = mode.ToFps();
        _lastLoggedTimecodeFps = 0;
    }

    public LtcFrameProcessingResult Process(LtcFrameReceivedEventArgs frame, TimecodeFpsMode mode)
    {
        double resolvedFps = _fpsSelector.Resolve(mode, frame.Fps, frame.Timecode.DropFrame);
        if (resolvedFps > 0)
            _lastTimecodeFps = resolvedFps;

        double resolvedSeconds = _lastTimecodeFps > 0
            ? frame.Timecode.ToRealSeconds(_lastTimecodeFps)
            : frame.RealTimeSeconds;

        TimecodeFrameDiagnosticResult diagnostic = _lastTimecodeFps > 0
            ? _diagnostics.Analyze(resolvedSeconds, _lastTimecodeFps)
            : new TimecodeFrameDiagnosticResult(TimecodeFrameDiagnosticStatus.Initial, 0.0, 0.0);

        bool shouldLogFps = Math.Abs(_lastTimecodeFps - _lastLoggedTimecodeFps) >= 0.01;
        if (shouldLogFps)
            _lastLoggedTimecodeFps = _lastTimecodeFps;

        return new LtcFrameProcessingResult(
            frame.Timecode.ToString(),
            TimecodeDisplayFormatter.RealTime(frame.RealTimeSeconds),
            resolvedSeconds,
            _lastTimecodeFps,
            FormatStatus(mode, frame.Fps, frame.Timecode.DropFrame, _lastTimecodeFps),
            diagnostic,
            TimecodeSyncFrameGate.ShouldApplySync(diagnostic.Status),
            shouldLogFps);
    }

    private static string FormatStatus(TimecodeFpsMode mode, double detectedFps, bool dropFrame, double resolvedFps)
    {
        string modeText = mode == TimecodeFpsMode.Auto ? "Auto" : "Fixed";
        string detected = TimecodeDisplayFormatter.FpsLabel(detectedFps, dropFrame);
        string resolved = resolvedFps > 0
            ? TimecodeDisplayFormatter.FpsLabel(resolvedFps, Math.Abs(resolvedFps - (30000.0 / 1001.0)) < 0.01)
            : "検出中";
        return $"{modeText}: {resolved}  detect: {detected}";
    }
}

public sealed record LtcFrameProcessingResult(
    string TimecodeText,
    string RealTimeText,
    double ResolvedSeconds,
    double ResolvedFps,
    string FormatText,
    TimecodeFrameDiagnosticResult Diagnostic,
    bool ShouldApplySync,
    bool ShouldLogFps);
