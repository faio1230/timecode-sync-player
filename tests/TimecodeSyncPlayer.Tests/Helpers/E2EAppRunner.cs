using FlaUI.Core.AutomationElements;
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
        if (!HasMpvLibrary(exeDir))
            return (exe, $"mpv-2.dll / libmpv-2.dll が見つかりません: {exeDir}");

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
        bool pausePlaybackIfNeeded = true)
    {
        string exeDir = Path.GetDirectoryName(exePath)!;
        var automation = new UIA3Automation();

        Process process = StartProcess(exePath, arguments, settingsFilePath);

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

    public static Process StartProcess(string exePath, string arguments, string? settingsFilePath)
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

    private static bool HasMpvLibrary(string directory) =>
        File.Exists(Path.Combine(directory, "mpv-2.dll")) ||
        File.Exists(Path.Combine(directory, "libmpv-2.dll"));

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
