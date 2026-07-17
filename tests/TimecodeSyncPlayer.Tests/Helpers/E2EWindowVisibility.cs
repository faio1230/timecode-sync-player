using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Exceptions;

namespace TimecodeSyncPlayer.Tests.Helpers;

internal static class E2EWindowVisibility
{
    public static bool IsVisible(Window window) =>
        IsVisible(
            isOffscreen: () => window.IsOffscreen,
            boundingRectangle: () => window.BoundingRectangle);

    internal static bool IsVisible(
        Func<bool> isOffscreen,
        Func<Rectangle> boundingRectangle)
    {
        try
        {
            return !isOffscreen();
        }
        catch (PropertyNotSupportedException)
        {
            Rectangle bounds = boundingRectangle();
            return bounds.Width > 0 && bounds.Height > 0;
        }
    }
}
