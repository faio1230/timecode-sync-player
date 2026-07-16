using System.Windows.Input;

namespace TimecodeSyncPlayer;

internal static class FullscreenInputPolicy
{
    public static bool ShouldClose(Key key) => key == Key.Escape;
}
