namespace TimecodeSyncPlayer;

internal static class LtcDeviceListRefreshPlanner
{
    public static int ResolveSelectedIndex(
        string? previousSelection,
        IReadOnlyList<string> deviceNames)
    {
        if (deviceNames.Count == 0)
            return -1;

        for (int i = 0; i < deviceNames.Count; i++)
        {
            if (deviceNames[i] == previousSelection)
                return i;
        }

        return 0;
    }
}
