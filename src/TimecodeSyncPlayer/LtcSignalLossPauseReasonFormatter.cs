namespace TimecodeSyncPlayer;

internal static class LtcSignalLossPauseReasonFormatter
{
    public static string Format(bool isPauseOwned) =>
        isPauseOwned ? "信号断で停止中" : string.Empty;
}
