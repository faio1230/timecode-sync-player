namespace TimecodeSyncPlayer;

internal static class TimecodeSyncFrameGate
{
    public static bool ShouldApplySync(TimecodeFrameDiagnosticStatus status) =>
        status is TimecodeFrameDiagnosticStatus.Initial
            or TimecodeFrameDiagnosticStatus.Normal;
}
