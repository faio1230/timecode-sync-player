namespace TimecodeSyncPlayer;

public sealed class TimecodeFrameDiagnostics
{
    private double? _lastSeconds;

    public TimecodeFrameDiagnosticResult Analyze(double seconds, double fps)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || !double.IsFinite(fps) || fps <= 0)
            return new TimecodeFrameDiagnosticResult(TimecodeFrameDiagnosticStatus.Invalid, 0.0, 0.0);

        if (_lastSeconds == null)
        {
            _lastSeconds = seconds;
            return new TimecodeFrameDiagnosticResult(TimecodeFrameDiagnosticStatus.Initial, 0.0, 0.0);
        }

        double deltaSeconds = seconds - _lastSeconds.Value;
        double deltaFrames = deltaSeconds * fps;
        _lastSeconds = seconds;

        TimecodeFrameDiagnosticStatus status =
            deltaFrames switch
            {
                < -2.5 => TimecodeFrameDiagnosticStatus.Jump,
                < -0.5 => TimecodeFrameDiagnosticStatus.Reverse,
                < 0.5 => TimecodeFrameDiagnosticStatus.Duplicate,
                > 2.5 => TimecodeFrameDiagnosticStatus.Jump,
                _ => TimecodeFrameDiagnosticStatus.Normal
            };

        return new TimecodeFrameDiagnosticResult(status, deltaSeconds, deltaFrames);
    }

    public void Reset()
    {
        _lastSeconds = null;
    }
}

public enum TimecodeFrameDiagnosticStatus
{
    Initial,
    Normal,
    Duplicate,
    Jump,
    Reverse,
    Invalid
}

public sealed record TimecodeFrameDiagnosticResult(
    TimecodeFrameDiagnosticStatus Status,
    double DeltaSeconds,
    double DeltaFrames);
