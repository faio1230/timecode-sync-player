namespace TimecodeSyncPlayer;

internal readonly record struct DisplayBounds(int Left, int Top, int Width, int Height);

internal sealed record DisplayTarget(string DeviceName, DisplayBounds Bounds, bool IsPrimary)
{
    public string DisplayName => IsPrimary ? $"{DeviceName} (Primary)" : DeviceName;
}

internal static class DisplaySelectionPolicy
{
    public static DisplayTarget? Select(
        IReadOnlyList<DisplayTarget> displays,
        string? savedDeviceName)
    {
        if (!string.IsNullOrWhiteSpace(savedDeviceName))
        {
            DisplayTarget? saved = displays.FirstOrDefault(display =>
                string.Equals(display.DeviceName, savedDeviceName, StringComparison.OrdinalIgnoreCase));
            if (saved != null)
                return saved;
        }

        return displays.FirstOrDefault(display => display.IsPrimary)
            ?? displays.FirstOrDefault();
    }

    public static bool ShouldClose(
        DisplayTarget target,
        IReadOnlyList<DisplayTarget> currentDisplays) =>
        !currentDisplays.Any(display =>
            string.Equals(display.DeviceName, target.DeviceName, StringComparison.OrdinalIgnoreCase)
            && display.Bounds == target.Bounds);
}
