using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GpuOutputProbe;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Keep these branches before Application, display enumeration, or native graphics initialization.
        if (args.Length == 1 && args[0] == "--self-test") return ManagedSelfTests.Run();
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine("GpuOutputProbe --mode common|split --output fullscreen|spout|both --width 1920 --height 1080 --fps 60 --seconds 32 --warmup 5 --log-dir <new-directory> --sender <unique-name> --monitor-index 0 [--windowed] [--display-pacing tick|ready|vsync|vblank (default tick; ready requires split/both/present-wait-ms 0/fixed plan; vsync and vblank require fullscreen output/present-wait-ms 0/fixed plan)] [--present-margin-ms 0.5..8 (default 3; non-default requires vblank pacing)] [--compose-align off|vblank (default off; vblank requires vblank pacing with fullscreen output)] [--compose-lead-ms 0.5..8 (default 1.5; non-default requires compose-align vblank)] [--mutex-wait-ms 0..8 (default 0)] [--present-wait-ms 0..1 (default 0; nonzero requires fullscreen)] [--present-wait-plan fixed|abba|baab (default fixed; segmented requires fullscreen, present-wait-ms 0, Seconds/4 > 2*Warmup)] [--send-phase-ms <0..less-than-one-period; default 0; nonzero requires split+Spout>] [--copy-retry off|signal (default off; signal requires split+Spout)] [--source-sync keyed|fence (default keyed; fence requires split+Spout, D3D11.4 fences, and --copy-retry off)] [--source pattern|contract-fake (default pattern; contract-fake feeds the compose from an IVideoSource fake at 30 fps)]");
            Console.WriteLine("Read-only display enumeration: --list-displays. Managed tests without GPU initialization: --self-test.");
            return 0;
        }
        try
        {
            if (args.Length == 1 && args[0] == "--validate-shaders")
            {
                ShaderPipeline.ValidateShaders();
                Console.WriteLine("HLSL compilation passed; no GPU device was created.");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--list-displays")
            {
                EnablePerMonitorDpi();
                Console.WriteLine(JsonSerializer.Serialize(Displays.Enumerate(), ProbeLog.Json));
                return 0;
            }
            var options = Options.Parse(args);
            if (Directory.Exists(options.LogDir) || File.Exists(options.LogDir))
                throw new ArgumentException("Log path already exists; select a new directory: " + options.LogDir);
            EnablePerMonitorDpi();
            var displays = Displays.Enumerate();
            var display = displays.SingleOrDefault(d => d.Index == options.MonitorIndex)
                ?? throw new ArgumentException("Selected monitor is not available. Use --list-displays.");
            Directory.CreateDirectory(Path.GetDirectoryName(options.LogDir)!);
            // CreateDirectoryW fails if the final directory already exists, including a startup race.
            if (!UiNative.CreateDirectory(options.LogDir, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not exclusively create new log directory: " + options.LogDir);
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new OperatorWindow(options, display, displays);
            app.MainWindow = window;
            window.Show();
            return app.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void EnablePerMonitorDpi()
    {
        var perMonitorV2 = new IntPtr(-4);
        bool IsPerMonitorV2() => UiNative.AreDpiAwarenessContextsEqual(UiNative.GetThreadDpiAwarenessContext(), perMonitorV2);
        // An embedded apphost manifest normally establishes this before managed startup.
        // SetProcessDpiAwarenessContext then reports access denied, so verify first.
        if (IsPerMonitorV2()) return;
        bool changed = UiNative.SetProcessDpiAwarenessContext(perMonitorV2);
        int error = changed ? 0 : Marshal.GetLastWin32Error();
        if (IsPerMonitorV2()) return;
        throw new Win32Exception(error, "PerMonitorV2 DPI awareness is required before display enumeration/WPF startup. Run the generated GpuOutputProbe.exe directly; dotnet GpuOutputProbe.dll can inherit a different host manifest.");
    }
}

internal sealed class OperatorWindow : Window
{
    private readonly Options options;
    private readonly DisplayInfo display;
    private readonly IReadOnlyList<DisplayInfo> displays;
    private readonly TextBlock status;
    private readonly Button closeButton;
    private readonly Button forceButton;
    private ProbeEngine? engine;
    private VideoWindow? video;
    private Window? exitDialog;
    private bool started, draining, allowClose;

    public OperatorWindow(Options options, DisplayInfo display, IReadOnlyList<DisplayInfo> displays)
    {
        this.options = options; this.display = display; this.displays = displays;
        Title = "GPU出力実証 — 操作";
        Width = 570; Height = 300; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = $"{options.Mode} / {options.Output} / {options.Width}×{options.Height} / {options.Fps:0.###} Hz", FontWeight = FontWeights.Bold, FontSize = 17 });
        panel.Children.Add(new TextBlock { Text = $"display pacing {options.DisplayPacing} / present margin {options.PresentMarginMs:0.###} ms / compose align {options.ComposeAlign} / lead {options.ComposeLeadMs:0.###} ms / source sync {options.SourceSync} / mutex request {options.MutexWaitMs} ms / send phase {options.SendPhaseMs:0.###} ms / copy retry {options.CopyRetry} / source {options.Source}", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = options.PresentWaitPlan == "fixed" ? $"present plan fixed / request {options.PresentWaitMs} ms" : $"present plan {options.PresentWaitPlan} / scheduled requests {(options.PresentWaitPlan == "abba" ? "0→1→1→0" : "1→0→0→1")} ms" });
        panel.Children.Add(new TextBlock { Text = $"表示先 {display.Index}: {display.DeviceName}\n期間 {options.Seconds:0.###} 秒　ログ: {options.LogDir}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 12) });
        status = new TextBlock { Text = "準備中", TextWrapping = TextWrapping.Wrap, Height = 75 };
        panel.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        closeButton = new Button { Content = "終了…", Padding = new Thickness(16, 6, 16, 6), MinWidth = 100 };
        closeButton.Click += (_, _) => RequestClose();
        forceButton = new Button { Content = "この実証アプリを強制終了", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(12, 0, 0, 0), Visibility = Visibility.Collapsed };
        forceButton.Click += (_, _) => ForceExit();
        buttons.Children.Add(closeButton); buttons.Children.Add(forceButton); panel.Children.Add(buttons);
        Content = panel;
        Closing += OnClosing;
        ContentRendered += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        if (started || allowClose) return;
        started = true;
        try
        {
            IntPtr hwnd = IntPtr.Zero;
            if (options.HasDisplay)
            {
                video = new VideoWindow(display, options.Windowed, RequestClose);
                video.Show();
                video.UpdateLayout();
                hwnd = video.VideoHandle;
                if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Video HWND was not created.");
                // Keep stop/force controls accessible even on a single monitor in fullscreen mode.
                if (!options.Windowed) Topmost = true;
                Activate();
            }
            engine = new ProbeEngine(options, display, displays, hwnd, SetStatus);
            if (draining) engine.RequestStop();
            await Task.Run(() => engine.RunAsync());
            Complete(engine.Failed ? 1 : 0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            SetStatus("異常: " + ex.Message);
            if (engine == null || engine.Finished) Complete(1);
            else
            {
                draining = true;
                engine.RequestStop();
                closeButton.Content = "終了状態を確認…";
                forceButton.Visibility = Visibility.Visible;
                SetStatus("終了の完了を確認できません。映像領域を保持しています。必要なら強制終了してください。 " + ex.Message);
            }
        }
    }

    private void SetStatus(string message)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!allowClose) status.Text = message;
        }));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        RequestClose();
    }

    private void RequestClose()
    {
        if (allowClose) return;
        if (exitDialog != null) { exitDialog.Activate(); return; }
        var dialog = new Window
        {
            Owner = this, Title = "実証アプリを終了", Width = 540, Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = Topmost
        };
        exitDialog = dialog;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = draining ? "GPU処理・資源解放の完了を待っています。" : "通常終了は処理完了とログ保存を待ちます。選択中も出力を続けます。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = new Button { Content = draining ? "待機を続ける" : "出力を続ける", IsCancel = true, IsDefault = true, Padding = new Thickness(10, 6, 10, 6) };
        cancel.Click += (_, _) => dialog.Close();
        var normal = new Button { Content = "通常終了", IsEnabled = !draining, Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        normal.Click += (_, _) => { dialog.Close(); BeginDrain(); };
        var force = new Button { Content = "このアプリを強制終了", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        force.Click += (_, _) => ForceExit();
        buttons.Children.Add(cancel); buttons.Children.Add(normal); buttons.Children.Add(force);
        panel.Children.Add(buttons); dialog.Content = panel;
        dialog.Closed += (_, _) => exitDialog = null;
        dialog.ContentRendered += (_, _) => cancel.Focus();
        dialog.Show();
    }

    private void BeginDrain()
    {
        draining = true;
        closeButton.Content = "終了状態を確認…";
        forceButton.Visibility = Visibility.Visible;
        status.Text = "新規処理を停止し、GPU処理・資源解放・ログ保存を待っています。";
        engine?.RequestStop();
    }

    private static void ForceExit()
    {
        // Kill only this process, without running potentially blocked native cleanup or awaiting workers.
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }

    private void Complete(int exitCode)
    {
        allowClose = true;
        exitDialog?.Close();
        video?.CloseAfterCompletion();
        Close();
        Application.Current.Shutdown(exitCode);
    }
}

internal sealed class VideoWindow : Window
{
    private readonly VideoHost host = new();
    private bool allowClose;
    public IntPtr VideoHandle => host.Handle;

    public VideoWindow(DisplayInfo display, bool windowed, Action requestClose)
    {
        Title = "GPU出力実証 — 映像";
        Background = Brushes.Black;
        Content = host;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = windowed ? WindowStyle.SingleBorderWindow : WindowStyle.None;
        Width = windowed ? Math.Min(960, display.Width * .8) : display.Width;
        Height = windowed ? Math.Min(600, display.Height * .8) : display.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Closing += (_, e) => { if (!allowClose) { e.Cancel = true; requestClose(); } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; requestClose(); } };
        SourceInitialized += (_, _) =>
        {
            // DXGI reports physical pixels; avoid translating monitor bounds through WPF's DIP coordinates.
            int width = windowed ? Math.Min(960, (int)(display.Width * .8)) : display.Width;
            int height = windowed ? Math.Min(600, (int)(display.Height * .8)) : display.Height;
            int x = display.X + (display.Width - width) / 2;
            int y = display.Y + (display.Height - height) / 2;
            if (!UiNative.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, x, y, width, height, 0x0004 | 0x0010))
                throw new Win32Exception();
        };
    }

    public void CloseAfterCompletion() { allowClose = true; Close(); }
}

internal sealed class VideoHost : HwndHost
{
    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var child = UiNative.CreateWindowEx(0, "STATIC", "GPU image", 0x40000000 | 0x10000000 | 0x04000000 | 0x02000000,
            0, 0, Math.Max(1, (int)ActualWidth), Math.Max(1, (int)ActualHeight), hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (child == IntPtr.Zero) throw new Win32Exception();
        return new HandleRef(this, child);
    }
    protected override void DestroyWindowCore(HandleRef hwnd) => UiNative.DestroyWindow(hwnd.Handle);
}

internal static class UiNative
{
    [DllImport("user32.dll")]
    internal static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateDirectory(string path, IntPtr securityAttributes);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}


