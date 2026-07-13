namespace TimecodeSyncPlayer;

public sealed record SpoutStartupState(bool IsButtonEnabled, string? ToggleLabel)
{
    public static SpoutStartupState FromInitializationResult(bool initialized)
    {
        return initialized
            ? new SpoutStartupState(true, null)
            : new SpoutStartupState(false, "Spout N/A");
    }
}
