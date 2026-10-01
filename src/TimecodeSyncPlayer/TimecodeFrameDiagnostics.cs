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

        return new TimecodeFrameDiagnosticResult(Classify(deltaFrames), deltaSeconds, deltaFrames);
    }

    /// <summary>
    /// 差のフレーム数での分類（Jump = ±2.5 フレーム超、Reverse = −2.5 以上 −0.5 未満、Duplicate = −0.5 以上 +0.5 未満）。
    /// v0.6.1: 層 2 の分類（LtcSyncController の受理済みの流れに対する分類）も同じ関数を使う（しきい値の定義を 1 つにする）。
    /// </summary>
    internal static TimecodeFrameDiagnosticStatus Classify(double deltaFrames) =>
        deltaFrames switch
        {
            < -2.5 => TimecodeFrameDiagnosticStatus.Jump,
            < -0.5 => TimecodeFrameDiagnosticStatus.Reverse,
            < 0.5 => TimecodeFrameDiagnosticStatus.Duplicate,
            > 2.5 => TimecodeFrameDiagnosticStatus.Jump,
            _ => TimecodeFrameDiagnosticStatus.Normal
        };

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
