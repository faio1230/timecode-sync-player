using System.Windows;

namespace TimecodeSyncPlayer;

public sealed record TimelineStartupState(Visibility ContainerVisibility, string ToggleLabel)
{
    public static TimelineStartupState FromVisibility(bool isVisible)
    {
        return isVisible
            ? new TimelineStartupState(Visibility.Visible, "Timeline ON")
            : new TimelineStartupState(Visibility.Collapsed, "Timeline OFF");
    }
}
