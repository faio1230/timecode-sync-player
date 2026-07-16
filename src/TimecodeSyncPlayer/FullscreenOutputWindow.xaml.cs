using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace TimecodeSyncPlayer;

internal partial class FullscreenOutputWindow : Window
{
    private const int WindowMessageDisplayChange = 0x007E;
    private const uint SetWindowPositionShowWindow = 0x0040;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private static readonly IntPtr TopmostWindow = new(-1);

    private readonly DisplayTarget _target;
    private readonly IDisplayCatalog _displayCatalog;
    private HwndSource? _source;

    public FullscreenOutputWindow(
        DisplayTarget target,
        IDisplayCatalog displayCatalog,
        ImageSource? initialImage)
    {
        _target = target;
        _displayCatalog = displayCatalog;
        InitializeComponent();
        FullscreenImage.Source = initialImage;
    }

    public void UpdateBitmap(ImageSource bitmap) => FullscreenImage.Source = bitmap;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _source?.AddHook(WindowProcedure);
        PositionOnTargetDisplay();
    }

    protected override void OnClosed(EventArgs e)
    {
        _source?.RemoveHook(WindowProcedure);
        _source = null;
        FullscreenImage.Source = null;
        base.OnClosed(e);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => PositionOnTargetDisplay();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!FullscreenInputPolicy.ShouldClose(e.Key))
            return;

        e.Handled = true;
        Close();
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    private IntPtr WindowProcedure(
        IntPtr window,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message == WindowMessageDisplayChange
            && DisplaySelectionPolicy.ShouldClose(_target, _displayCatalog.GetDisplays()))
        {
            _ = Dispatcher.BeginInvoke(Close);
        }

        return IntPtr.Zero;
    }

    private void PositionOnTargetDisplay()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        DisplayBounds bounds = _target.Bounds;
        _ = SetWindowPos(
            handle,
            TopmostWindow,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SetWindowPositionNoActivate | SetWindowPositionShowWindow);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
