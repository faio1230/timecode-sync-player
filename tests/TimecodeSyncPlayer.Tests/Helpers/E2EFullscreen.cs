using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;

namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// v0.6.6 R-8: 全画面を出す E2E の共通の手順。主画面（作業中の画面）を選んでいると FULLSCREEN の後に確認の画面が出るので、
/// 試験の側で「出す」を押す（製品に試験用の口は足さない）。主画面でない出力先なら確認は出ず、そのまま全画面になる。
/// </summary>
internal static class E2EFullscreen
{
    public const string FullscreenButtonId = "BtnFullscreen";
    public const string FullscreenWindowId = "FullscreenOutputWindow";
    public const string ConfirmDialogId = "FullscreenConfirmDialog";
    public const string ConfirmShowId = "BtnFullscreenConfirmShow";
    public const string ConfirmCancelId = "BtnFullscreenConfirmCancel";

    public static Window? FindFullscreenWindow(Window mainWindow) => FindTopLevel(mainWindow, FullscreenWindowId);

    public static Window? FindConfirmDialog(Window mainWindow) => FindTopLevel(mainWindow, ConfirmDialogId);

    /// <summary>
    /// FULLSCREEN を押し、確認の画面が出たら「出す」を押す。全画面の窓が出るまで待つ。
    /// 戻り値は確認の画面が出たか（主画面に出した）。
    /// </summary>
    public static bool Open(Window mainWindow, TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(5);
        FullscreenButton(mainWindow).Invoke();

        Window? dialog = null;
        E2EAssert.WaitUntil(() =>
        {
            if (FindFullscreenWindow(mainWindow) != null)
                return true;
            dialog = FindConfirmDialog(mainWindow);
            return dialog != null;
        }, limit);

        bool confirmed = false;
        if (dialog != null)
        {
            ConfirmButton(dialog, ConfirmShowId).Invoke();
            confirmed = true;
        }

        E2EAssert.WaitUntil(() => FindFullscreenWindow(mainWindow) != null, limit);
        return confirmed;
    }

    public static Button FullscreenButton(Window mainWindow)
        => mainWindow.FindFirstDescendant(cf => cf.ByAutomationId(FullscreenButtonId)).AsButton();

    public static Button ConfirmButton(Window dialog, string automationId)
        => dialog.FindFirstDescendant(cf => cf.ByAutomationId(automationId)).AsButton();

    /// <summary>
    /// 全画面の窓へ Esc を送る（押して離す）。SendInput は前面の制約を受けるため、窓の HWND へ直接送る（ExitDialog の Enter と同じ作法）。
    /// </summary>
    public static void PressEscape(Window window)
    {
        IntPtr hwnd = window.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("全画面の窓の HWND を取得できない");
        PostMessage(hwnd, WindowMessageKeyDown, EscapeVirtualKey, EscapeKeyDownLParam);
        PostMessage(hwnd, WindowMessageKeyUp, EscapeVirtualKey, EscapeKeyUpLParam);
    }

    private static Window? FindTopLevel(Window mainWindow, string automationId)
        => mainWindow.Automation.GetDesktop()
            .FindFirstDescendant(cf => cf.ByAutomationId(automationId))
            ?.AsWindow();

    private const int WindowMessageKeyDown = 0x0100;
    private const int WindowMessageKeyUp = 0x0101;
    private static readonly IntPtr EscapeVirtualKey = new(0x1B);
    private static readonly IntPtr EscapeKeyDownLParam = new(0x00010001);
    private static readonly IntPtr EscapeKeyUpLParam = new(unchecked((long)0xC0010001));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
