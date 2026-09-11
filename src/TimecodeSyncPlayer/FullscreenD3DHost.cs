using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace TimecodeSyncPlayer;

/// <summary>
/// 全画面ウィンドウ内の子 HWND。Gpu 出力時はこの子ウィンドウへ swapchain を接続する。
/// 操作 UI は重ねない（WPF と D3D の airspace 回避）。
/// </summary>
internal sealed class FullscreenD3DHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;

    public event Action<IntPtr>? ChildHwndReady;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        IntPtr hwnd = CreateWindowEx(
            0, "static", string.Empty,
            WsChild | WsVisible,
            0, 0, 16, 16,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("全画面の子 HWND を作成できませんでした。");
        ChildHwndReady?.Invoke(hwnd);
        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
            DestroyWindow(hwnd.Handle);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);
}
