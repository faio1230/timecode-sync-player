using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TimecodeSyncPlayer.Tests.Helpers;

internal sealed class E2EAppRunner : IDisposable
{
    private readonly UIA3Automation _automation;
    private readonly Process _process;

    private E2EAppRunner(UIA3Automation automation, Process process, Window mainWindow)
    {
        _automation = automation;
        _process = process;
        MainWindow = mainWindow;
    }

    public Window MainWindow { get; }

    public Process Process => _process;

    public bool RequestMainWindowClose()
    {
        IntPtr handle = MainWindow.Properties.NativeWindowHandle.Value;
        GetWindowThreadProcessId(handle, out int ownerId);
        return ownerId == _process.Id && PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 段階 5.1: 閉じる要求は ExitDialog を経由する。ダイアログの BtnExitNormal を押して
    /// プロセスの終了を待つ（timeout 内に終了すれば true）。既に終了済みなら何もしない。
    /// </summary>
    public bool ExitNormally(TimeSpan timeout)
    {
        if (_process.HasExited) return true;
        if (!RequestMainWindowClose()) return false;
        DateTime deadline = DateTime.UtcNow + timeout;
        bool normalPressed = false;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (_process.WaitForExit(100)) return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            if (normalPressed) continue;
            try
            {
                Window? dialog = FindTopLevelWindow("ExitDialog");
                if (dialog == null) continue;
                Button? normal = dialog.FindFirstDescendant(cf => cf.ByAutomationId("BtnExitNormal"))?.AsButton();
                if (normal == null) continue;
                normal.Invoke();
                normalPressed = true;
            }
            catch (Exception)
            {
                // ダイアログが既に閉じた・UIA が一時的に失敗した場合は終了待ちを続ける。
            }
        }
        try { return _process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    public static (string ExePath, string? SkipReason) ResolvePrereqs()
    {
        string exe;
        try
        {
            exe = LocateExe();
        }
        catch (FileNotFoundException ex)
        {
            return ("", ex.Message);
        }

        string exeDir = Path.GetDirectoryName(exe)!;
        if (!File.Exists(Path.Combine(exeDir, "tcs_gstreamer.dll")))
            return (exe, $"tcs_gstreamer.dll が見つかりません（build-shim を実行してください）: {exeDir}");

        if (GstRuntimeBinDirectory() is null)
            return (exe, "GStreamer ランタイムが見つかりません（GSTREAMER_1_0_ROOT_MSVC_X86_64 を確認してください）。");

        if (!TestVideoFactory.FfmpegAvailable())
            return (exe, "ffmpeg が PATH にありません。");

        try
        {
            _ = TestVideoFactory.GetOrCreate();
        }
        catch (Exception ex)
        {
            return (exe, $"テスト動画の生成に失敗しました: {ex.Message}");
        }

        return (exe, null);
    }

    public static E2EAppRunner Start(string exePath, string arguments)
        => Start(exePath, arguments, settingsFilePath: null);

    public static E2EAppRunner Start(
        string exePath,
        string arguments,
        string? settingsFilePath,
        bool pausePlaybackIfNeeded = true,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        string exeDir = Path.GetDirectoryName(exePath)!;
        var automation = new UIA3Automation();

        Process process = StartProcess(exePath, arguments, settingsFilePath, environment);

        try
        {
            IntPtr mainWindowHandle = Retry.While(
                () =>
                {
                    try
                    {
                        return FindMainWindowHandle(process.Id);
                    }
                    catch
                    {
                        return IntPtr.Zero;
                    }
                },
                handle => handle == IntPtr.Zero,
                timeout: TimeSpan.FromSeconds(5),
                interval: TimeSpan.FromMilliseconds(200)
            ).Result;

            if (mainWindowHandle == IntPtr.Zero)
                throw new TimeoutException("TimecodeSyncPlayer のメインウィンドウが5秒以内に表示されませんでした。");

            var window = automation.FromHandle(mainWindowHandle).AsWindow();
            E2EAssert.WaitUntil(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("BtnPlay")) != null,
                TimeSpan.FromSeconds(5));
            if (pausePlaybackIfNeeded)
                PausePlaybackIfNeeded(window);
            return new E2EAppRunner(automation, process, window);
        }
        catch
        {
            KillProcess(process);
            process.Dispose();
            automation.Dispose();
            throw;
        }
    }

    public static Process StartProcess(string exePath, string arguments)
        => StartProcess(exePath, arguments, settingsFilePath: null);

    public static Process StartProcess(string exePath, string arguments, string? settingsFilePath,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        string exeDir = Path.GetDirectoryName(exePath)!;
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            WorkingDirectory = exeDir,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        if (environment != null)
        {
            foreach (var entry in environment)
            {
                if (entry.Value == null) startInfo.Environment.Remove(entry.Key);
                else startInfo.Environment[entry.Key] = entry.Value;
            }
        }
        string? settingsDirectory = null;
        if (string.IsNullOrWhiteSpace(settingsFilePath))
        {
            settingsDirectory = E2ESettingsIsolation.Configure(startInfo);
        }
        else
        {
            string fullSettingsPath = Path.GetFullPath(settingsFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullSettingsPath)!);
            startInfo.Environment[AppSettingsManager.SettingsPathEnvironmentVariable] = fullSettingsPath;
        }

        // T10: 精度測定の run では shim の内訳ログ（stderr）を残す。
        bool captureStreams = AppStreamCapture.IsEnabled;
        if (captureStreams)
            AppStreamCapture.Configure(startInfo);

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("TimecodeSyncPlayer.exe の起動に失敗しました。");
        }
        catch
        {
            E2ESettingsIsolation.Delete(settingsDirectory);
            throw;
        }

        if (captureStreams)
            AppStreamCapture.Attach(process);

        if (settingsDirectory != null)
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => E2ESettingsIsolation.Delete(settingsDirectory);
            if (process.HasExited)
                E2ESettingsIsolation.Delete(settingsDirectory);
        }

        return process;
    }

    public Button Button(string automationId)
        => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)).AsButton();

    /// <summary>デスクトップ上のトップレベルウィンドウを AutomationId で探す（ExitDialog 等）。</summary>
    public Window? FindTopLevelWindow(string automationId)
        => _automation.GetDesktop()
            .FindFirstDescendant(cf => cf.ByAutomationId(automationId))
            ?.AsWindow();

    public Window WaitForTopLevelWindow(string automationId, TimeSpan timeout)
    {
        Window? found = null;
        E2EAssert.WaitUntil(() =>
        {
            found = FindTopLevelWindow(automationId);
            return found != null;
        }, timeout);
        return found!;
    }

    /// <summary>
    /// トップレベルウィンドウをタイトルで探す（ネイティブダイアログ等）。
    /// デスクトップ全体の子孫を名前一致だけで拾うと、同じ文字列を持つテキスト要素などを
    /// ウィンドウと誤認するため、ControlType=Window の要素だけを対象にする。
    /// 所有ダイアログはデスクトップ直下に現れないことがあるため、子孫全体を検索する。
    /// </summary>
    public Window? FindWindowByName(string name)
        => _automation.GetDesktop()
            .FindAllDescendants(cf => cf.ByControlType(ControlType.Window))
            .FirstOrDefault(window => window.Properties.Name.ValueOrDefault == name)
            ?.AsWindow();

    public ComboBox Combo(string automationId)
        => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)).AsComboBox();

    public Slider Slider(string automationId)
        => MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId)).AsSlider();

    public string Text(string automationId)
    {
        var el = MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (el == null) return "";

        var textPattern = el.Patterns.Text.PatternOrDefault;
        if (textPattern != null)
        {
            string text = textPattern.DocumentRange.GetText(-1).Trim();
            if (!string.IsNullOrEmpty(text))
                return text;
        }

        return el.Name.Trim();
    }

    public void Dispose()
    {
        KillProcess(_process);
        _process.Dispose();
        _automation.Dispose();
    }

    private static string LocateExe()
    {
        string? env = Environment.GetEnvironmentVariable("TIMECODE_SYNC_PLAYER_E2E_APP_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        string sameDir = Path.Combine(AppContext.BaseDirectory, "TimecodeSyncPlayer.exe");
        if (File.Exists(sameDir))
            return sameDir;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            foreach (string config in new[] { "Debug", "Release" })
            {
                string candidate = Path.Combine(
                    dir.FullName, "src", "TimecodeSyncPlayer", "bin", config,
                    "net8.0-windows", "TimecodeSyncPlayer.exe");
                if (File.Exists(candidate))
                    return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "TimecodeSyncPlayer.exe が見つかりません。src/TimecodeSyncPlayer をビルドするか TIMECODE_SYNC_PLAYER_E2E_APP_PATH を設定してください。");
    }

    private static string? GstRuntimeBinDirectory()
    {
        string? root = Environment.GetEnvironmentVariable("GSTREAMER_1_0_ROOT_MSVC_X86_64");
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "gstreamer", "1.0", "msvc_x86_64");
        }
        string bin = Path.Combine(root, "bin");
        return File.Exists(Path.Combine(bin, "gstreamer-1.0-0.dll")) ||
               File.Exists(Path.Combine(bin, "gstreamer-1.0.dll"))
            ? bin : null;
    }

    public static void KillProcess(Process? process)
    {
        if (process == null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(milliseconds: 5000);
            }
        }
        catch
        {
        }
    }

    private static void PausePlaybackIfNeeded(Window window)
    {
        try
        {
            Button? playButton = window
                .FindFirstDescendant(cf => cf.ByAutomationId("BtnPlay"))
                ?.AsButton();

            if (playButton?.Name != "⏸")
                return;

            playButton.Invoke();
            E2EAssert.WaitUntil(() => playButton.Name == "▶", TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
    }

    private static IntPtr FindMainWindowHandle(int processId)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out int windowProcessId);
            if (windowProcessId != processId)
                return true;

            string title = GetWindowTitle(handle);
            if (title == ApplicationVersion.WindowTitle)
            {
                found = handle;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        var title = new StringBuilder(256);
        _ = GetWindowText(handle, title, title.Capacity);
        return title.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
