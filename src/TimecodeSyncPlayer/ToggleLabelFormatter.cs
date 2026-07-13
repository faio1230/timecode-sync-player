namespace TimecodeSyncPlayer;

internal static class ToggleLabelFormatter
{
    public static string Format(bool enabled, string onLabel, string offLabel)
        => enabled ? onLabel : offLabel;
}
