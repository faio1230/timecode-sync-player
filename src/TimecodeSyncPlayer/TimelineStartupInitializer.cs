namespace TimecodeSyncPlayer;

internal static class TimelineStartupInitializer
{
    public static TimelineStartupState CreateState(bool isVisible)
    {
        return TimelineStartupState.FromVisibility(isVisible);
    }
}
