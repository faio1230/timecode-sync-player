using System.Windows.Input;

namespace Erytheia;

internal static class FullscreenInputPolicy
{
    public static bool ShouldClose(Key key) => key == Key.Escape;
}
