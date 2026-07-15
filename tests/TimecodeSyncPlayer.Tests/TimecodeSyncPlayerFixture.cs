using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// テストクラス単位で TimecodeSyncPlayer.exe を起動し、FlaUI のウィンドウハンドルを提供する。
/// xUnit の IClassFixture&lt;TimecodeSyncPlayerFixture&gt; として使用する。
/// </summary>
public sealed class TimecodeSyncPlayerFixture : IAsyncLifetime
{
    private UIA3Automation?  _automation;
    private Process?         _process;
    private string?          _settingsDirectory;

    public Window?    MainWindow { get; private set; }
    public bool       Skipped    { get; private set; }
    public string?    SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        // 前提条件チェック
        string exePath = LocateExe();
        string exeDir  = Path.GetDirectoryName(exePath)!;

        if (!File.Exists(Path.Combine(exeDir, "mpv-2.dll")))
        {
            Skipped    = true;
            SkipReason = $"mpv-2.dll が見つかりません: {exeDir}";
            return;
        }

        if (!TestVideoFactory.FfmpegAvailable())
        {
            Skipped    = true;
            SkipReason = "ffmpeg が PATH にありません。";
            return;
        }

        string videoPath;
        try
        {
            videoPath = TestVideoFactory.GetOrCreate();
        }
        catch (Exception ex)
        {
            Skipped    = true;
            SkipReason = $"テスト動画の生成に失敗しました: {ex.Message}";
            return;
        }

        // アプリを --open で起動
        _automation = new UIA3Automation();
        // --vo null: GPU rendering を無効化（E2Eテストではデコード確認のみ行うため）
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"--vo null --open \"{videoPath}\" --playlist \"{videoPath}\"",
            WorkingDirectory = exeDir,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        _settingsDirectory = E2ESettingsIsolation.Configure(startInfo);
        try
        {
            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("TimecodeSyncPlayer.exe の起動に失敗しました。");
        }
        catch
        {
            E2ESettingsIsolation.Delete(_settingsDirectory);
            _settingsDirectory = null;
            throw;
        }

        // ウィンドウが表示されるまで最大5秒待機
        IntPtr mainWindowHandle = Retry.While(
            () =>
            {
                try
                {
                    return FindMainWindowHandle(_process.Id);
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

        MainWindow = _automation.FromHandle(mainWindowHandle).AsWindow();

        // 動画ロード完了を待機（mpvのデコード開始まで余裕を持たせる）
        await Task.Delay(5000);
        await PausePlaybackIfNeeded();
    }

    public Task DisposeAsync()
    {
        KillProcessTree(_process);
        _process?.Dispose();
        _automation?.Dispose();
        E2ESettingsIsolation.Delete(_settingsDirectory);
        return Task.CompletedTask;
    }

    private static void KillProcessTree(Process? process)
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
            // E2Eクリーンアップでは、既に終了済みのプロセスや権限差による例外を握り潰す。
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
            if (title == "Timecode Sync Player")
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

    private static string LocateExe()
    {
        // 環境変数で明示指定
        string? env = Environment.GetEnvironmentVariable("TIMECODE_SYNC_PLAYER_E2E_APP_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;

        // テストバイナリと同じディレクトリ（CI でコピーした場合）
        string sameDir = Path.Combine(AppContext.BaseDirectory, "TimecodeSyncPlayer.exe");
        if (File.Exists(sameDir)) return sameDir;

        // リポジトリルートからの相対パス探索
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            foreach (string config in new[] { "Debug", "Release" })  // Debug が開発標準
            {
                string candidate = Path.Combine(
                    dir.FullName, "src", "TimecodeSyncPlayer", "bin", config,
                    "net8.0-windows", "TimecodeSyncPlayer.exe");
                if (File.Exists(candidate)) return candidate;
            }
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "TimecodeSyncPlayer.exe が見つかりません。src/TimecodeSyncPlayer をビルドするか TIMECODE_SYNC_PLAYER_E2E_APP_PATH を設定してください。");
    }

    private async Task PausePlaybackIfNeeded()
    {
        try
        {
            Button? playButton = MainWindow?
                .FindFirstDescendant(cf => cf.ByAutomationId("BtnPlay"))
                ?.AsButton();

            if (playButton?.Name != "⏸")
                return;

            playButton.Invoke();
            await Task.Delay(300);
        }
        catch
        {
            // 初期停止はE2E安定化用の補助処理なので、失敗時は個別テストの検証に委ねる。
        }
    }
}
