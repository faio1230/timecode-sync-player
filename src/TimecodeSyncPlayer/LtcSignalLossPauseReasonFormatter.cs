namespace TimecodeSyncPlayer;

internal static class LtcSignalLossPauseReasonFormatter
{
    public static string Format(bool isPauseOwned, LtcSignalLossReason reason) =>
        isPauseOwned
            ? reason == LtcSignalLossReason.TimecodeHeld
                ? "タイムコード停止で停止中"
                : "信号断で停止中"
            : string.Empty;
}
