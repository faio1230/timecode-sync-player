namespace TimecodeSyncPlayer;

internal sealed record LtcDisplayState(
    string FormatText,
    string TimecodeForeground);

internal static class LtcDisplayStateFormatter
{
    private const string ActiveTimecodeForeground = "#55D86A";
    private const string LostTimecodeForeground = "#666666";

    public static LtcDisplayState Format(
        bool isMonitoring,
        bool isSignalLost,
        string normalFormatText) =>
        isMonitoring && isSignalLost
            ? new LtcDisplayState("NO SIGNAL", LostTimecodeForeground)
            : new LtcDisplayState(normalFormatText, ActiveTimecodeForeground);
}
