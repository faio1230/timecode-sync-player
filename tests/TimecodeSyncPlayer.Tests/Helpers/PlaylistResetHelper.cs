using System.Threading;
using FlaUI.Core.AutomationElements;

namespace TimecodeSyncPlayer.Tests.Helpers;

internal static class PlaylistResetHelper
{
    /// <summary>
    /// E2E前にウィンドウとPlaylist領域が存在することだけ確認する。
    /// UIAのSelect/Items操作は環境によって戻らないことがあるため、共通リセットでは行わない。
    /// </summary>
    public static void ResetPlaylistAndModes(Window window)
    {
        _ = window.FindFirstDescendant(cf => cf.ByAutomationId("PlaylistList"));
        Thread.Sleep(80);
    }
}
