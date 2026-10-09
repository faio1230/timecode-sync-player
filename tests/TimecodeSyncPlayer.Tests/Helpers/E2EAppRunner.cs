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
    /// <summary>出力トレースの環境変数。値はフォルダ名。</summary>
    private const string OutputTraceVariable = "TIMECODE_SYNC_PLAYER_OUTPUT_TRACE";

    private static readonly object TraceGate = new();
    private static int _traceLaunchNumber;

    private readonly UIA3Automation _automation;
    private readonly Process _process;

    private E2EAppRunner(UIA3Automation automation, Process process, Window mainWindow, AppGstEnvironment appEnvironment)
    {
        _automation = automation;
        _process = process;
        MainWindow = mainWindow;
        AppEnvironment = appEnvironment;
    }

    public Window MainWindow { get; }

    /// <summary>試験基盤の 9: アプリに渡した GStreamer まわりの環境（出力の 1 行は <see cref="AppGstEnvironment.Describe"/>）。</summary>
    public AppGstEnvironment AppEnvironment { get; }

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
        ExitRequestedUtc ??= DateTime.UtcNow;
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
                DateTime pressAt = DateTime.UtcNow;
                normal.Invoke();
                normalPressed = true;
                ExitPressedUtc ??= pressAt;
            }
            catch (Exception)
            {
                // ダイアログが既に閉じた・UIA が一時的に失敗した場合は終了待ちを続ける。
            }
        }
        try { return _process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    /// <summary>段 5b: 最初に閉じる要求（WM_CLOSE）を送った時刻（UTC）。</summary>
    public DateTime? ExitRequestedUtc { get; private set; }

    /// <summary>段 5b: 最初に終了ダイアログの BtnExitNormal を押した時刻（UTC）。</summary>
    public DateTime? ExitPressedUtc { get; private set; }

    /// <summary>
    /// 段 5b: 「終了を押してからプロセスが消えるまで」の秒数の記録（journal の app-exit-timing）。
    /// 起点は BtnExitNormal を押した時刻、押せなかったときは閉じる要求の時刻。終わりは
    /// <see cref="Process.ExitTime"/>。まだ終わっていなければ seconds は null で、waitedSeconds に
    /// その時点までの待ち時間（下限）を入れる。集計は scripts\LtcRunMetrics.psm1。
    /// </summary>
    public object DescribeExit(string phase)
    {
        DateTime? start = ExitPressedUtc ?? ExitRequestedUtc;
        bool exited;
        DateTime? exitedUtc = null;
        try
        {
            exited = _process.HasExited;
            if (exited) exitedUtc = _process.ExitTime.ToUniversalTime();
        }
        catch (InvalidOperationException)
        {
            exited = true;
        }
        double? seconds = start is { } from && exitedUtc is { } to ? Math.Round((to - from).TotalSeconds, 3) : null;
        double? waitedSeconds = start is { } since && !exited ? Math.Round((DateTime.UtcNow - since).TotalSeconds, 3) : null;
        return new
        {
            phase,
            exited,
            pressed = ExitPressedUtc is not null,
            requestedAtUtc = ExitRequestedUtc,
            pressedAtUtc = ExitPressedUtc,
            exitedAtUtc = exitedUtc,
            seconds,
            waitedSeconds,
        };
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

        if (GstRuntimeBinDirectory(exeDir) is null)
            return (exe, "GStreamer ランタイムが見つかりません（同梱の gstreamer\\bin か GSTREAMER_1_0_ROOT_MSVC_X86_64 を確認してください）。");

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

        (Process process, AppGstEnvironment appEnvironment) =
            StartProcessCore(exePath, arguments, settingsFilePath, environment);

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

            // インストール直後の初回起動は、プラグインの読み込みで UI スレッドが数秒ふさがり、
            // FromHandle が UIA の時間切れ（0x800705B4）で落ちることがある（2026-09-28 検証機）。30 秒まで取り直す。
            var window = Retry.WhileException(() => automation.FromHandle(mainWindowHandle).AsWindow(),
                timeout: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(500), throwOnTimeout: true).Result;
            E2EAssert.WaitUntil(
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("BtnPlay")) != null,
                TimeSpan.FromSeconds(5));
            if (pausePlaybackIfNeeded)
                PausePlaybackIfNeeded(window);
            return new E2EAppRunner(automation, process, window, appEnvironment);
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

    /// <summary>
    /// 出力トレースを起動ごとの別フォルダへ逃がす。アプリは manifest.json を新規作成でしか
    /// 書かないため、1 つのフォルダを共有すると 2 つ目以降のインスタンスが終了時に
    /// 「出力トレースの保存に失敗しました（manifest.json already exists）」で捨てられる。
    /// 呼び出し側が明示的にフォルダを指定しているときは触らない。
    /// </summary>
    private static void IsolateOutputTrace(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string?>? environment)
    {
        if (environment != null && environment.ContainsKey(OutputTraceVariable))
            return;
        if (!startInfo.Environment.TryGetValue(OutputTraceVariable, out string? root) || string.IsNullOrWhiteSpace(root))
            return;

        int number;
        lock (TraceGate)
        {
            number = ++_traceLaunchNumber;
        }

        string directory = Path.Combine(root, $"{DateTime.Now:HHmmss}-{number:D3}");
        Directory.CreateDirectory(directory);
        startInfo.Environment[OutputTraceVariable] = directory;
    }

    public static Process StartProcess(string exePath, string arguments, string? settingsFilePath,
        IReadOnlyDictionary<string, string?>? environment = null)
        => StartProcessCore(exePath, arguments, settingsFilePath, environment).Process;

    private static (Process Process, AppGstEnvironment Environment) StartProcessCore(
        string exePath, string arguments, string? settingsFilePath, IReadOnlyDictionary<string, string?>? environment)
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
        IsolateOutputTrace(startInfo, environment);

        // 試験基盤の 9: アプリは本番と同じ環境で起動させる。同梱の GStreamer があるときは、試験のプロセスが
        // 持つ GSTREAMER_1_0_ROOT_MSVC_X86_64 と PATH の GStreamer の bin を渡さない（呼び出し側が
        // 変数を明示したときだけ、その値を残す）。
        bool callerSetRoot = environment != null &&
            environment.TryGetValue(GstRootVariable, out string? explicitRoot) && explicitRoot != null;
        AppGstEnvironment appEnvironment = ApplyProductionGstEnvironment(
            startInfo.Environment, exeDir, callerSetRoot, HasGstCoreDll);
        Console.WriteLine(appEnvironment.Describe());

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

        return (process, appEnvironment);
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

    /// <summary>
    /// D18: 配布物は exe と同じディレクトリに GStreamer ランタイム（gstreamer\bin）を同梱する。
    /// 同梱があれば環境変数 GSTREAMER_1_0_ROOT_MSVC_X86_64 を要求しない。environmentRoot は
    /// 単体テストが両経路を固定するための入口（null なら環境変数を読む）。
    /// </summary>
    internal static string? GstRuntimeBinDirectory(string exeDir, string? environmentRoot = null)
    {
        string bundled = Path.Combine(exeDir, "gstreamer", "bin");
        if (HasGstCoreDll(bundled))
            return bundled;

        string? root = environmentRoot ?? Environment.GetEnvironmentVariable("GSTREAMER_1_0_ROOT_MSVC_X86_64");
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "gstreamer", "1.0", "msvc_x86_64");
        }
        string bin = Path.Combine(root, "bin");
        return HasGstCoreDll(bin) ? bin : null;
    }

    private static bool HasGstCoreDll(string bin)
        => File.Exists(Path.Combine(bin, "gstreamer-1.0-0.dll")) ||
           File.Exists(Path.Combine(bin, "gstreamer-1.0.dll"));

    internal const string GstRootVariable = "GSTREAMER_1_0_ROOT_MSVC_X86_64";

    /// <summary>
    /// 試験基盤の 9: アプリに渡す環境を本番に揃える。アプリは GSTREAMER_1_0_ROOT_MSVC_X86_64 を同梱の
    /// gstreamer\ より先に使う（GstNativeLibraryResolver.ResolveRoot）。試験のプロセスは shim を読むために
    /// この変数と PATH の先頭の gstreamer\bin を持つことがあり、そのまま継ぐと本番（変数なし）と違う構成になる。
    /// exe の隣に同梱の gstreamer\bin（コアの DLL 入り）があるときだけ、変数と、PATH のうち GStreamer の
    /// コアの DLL があるフォルダを外す（呼び出し側が変数を明示したときは変数を残す）。同梱が無いとき
    /// （開発機の Debug の exe）は触らない。アプリは変数か Program Files の GStreamer しか使えないため、
    /// 外すと変数でしか置いていない機体では起動できなくなる。
    /// </summary>
    internal static AppGstEnvironment ApplyProductionGstEnvironment(
        IDictionary<string, string?> environment, string exeDir, bool callerSetRoot, Func<string, bool> hasGstCoreDll)
    {
        bool bundled = hasGstCoreDll(Path.Combine(exeDir, "gstreamer", "bin"));
        bool rootRemoved = false;
        int pathEntriesRemoved = 0;
        if (bundled)
        {
            if (!callerSetRoot && environment.ContainsKey(GstRootVariable))
                rootRemoved = environment.Remove(GstRootVariable);
            if (environment.TryGetValue("PATH", out string? path) && path != null)
            {
                (string kept, int removed) = WithoutGstBinEntries(path, hasGstCoreDll);
                environment["PATH"] = kept;
                pathEntriesRemoved = removed;
            }
        }

        environment.TryGetValue(GstRootVariable, out string? root);
        environment.TryGetValue("PATH", out string? finalPath);
        int pathEntriesPassed = string.IsNullOrEmpty(finalPath)
            ? 0
            : finalPath.Split(Path.PathSeparator).Count(entry => IsGstBinEntry(entry, hasGstCoreDll));
        return new AppGstEnvironment(bundled, string.IsNullOrEmpty(root) ? null : root,
            rootRemoved, pathEntriesRemoved, pathEntriesPassed);
    }

    /// <summary>同梱の gstreamer\bin が exe の隣にあるときだけ、PATH から GStreamer の bin を外す（F7 の PATH 用）。</summary>
    internal static string PathForApp(string path, string exeDir, Func<string, bool>? hasGstCoreDll = null)
    {
        Func<string, bool> has = hasGstCoreDll ?? HasGstCoreDll;
        return has(Path.Combine(exeDir, "gstreamer", "bin")) ? WithoutGstBinEntries(path, has).Path : path;
    }

    internal static (string Path, int Removed) WithoutGstBinEntries(string path, Func<string, bool> hasGstCoreDll)
    {
        string[] entries = path.Split(Path.PathSeparator);
        List<string> kept = entries.Where(entry => !IsGstBinEntry(entry, hasGstCoreDll)).ToList();
        return (string.Join(Path.PathSeparator, kept), entries.Length - kept.Count);
    }

    private static bool IsGstBinEntry(string entry, Func<string, bool> hasGstCoreDll)
    {
        string dir = entry.Trim().Trim('"');
        if (dir.Length == 0) return false;
        try
        {
            return hasGstCoreDll(dir);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 試験基盤の 9: アプリのプロセスが読み込んだ GStreamer のコアの DLL のパスと出所。アプリは GStreamer の
    /// 出所をログに出さないため（棚卸し #48）、試験の側で読む。GStreamer を読み込んだ後
    /// （プロジェクトやクリップのロードの後）に呼ぶ。
    /// </summary>
    public (string? Path, string Origin) LoadedGstreamer()
    {
        string? loaded = null;
        try
        {
            _process.Refresh();
            foreach (ProcessModule module in _process.Modules)
            {
                string name = Path.GetFileName(module.FileName ?? "");
                if (name.Equals("gstreamer-1.0-0.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("gstreamer-1.0.dll", StringComparison.OrdinalIgnoreCase))
                {
                    loaded = module.FileName;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ($"<unreadable: {ex.Message}>", "unknown");
        }

        string programFilesBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "gstreamer", "1.0", "msvc_x86_64", "bin");
        return (loaded, ClassifyGstOrigin(loaded, Path.GetDirectoryName(_process.MainModule?.FileName ?? "") ?? "",
            AppEnvironment.RootPassed, programFilesBin));
    }

    /// <summary>読み込んだ DLL のフォルダから出所を決める（bundled / environment / programFiles / other / none）。</summary>
    internal static string ClassifyGstOrigin(string? loadedPath, string exeDir, string? rootPassed, string programFilesBin)
    {
        if (string.IsNullOrEmpty(loadedPath)) return "none";
        string? dir = Path.GetDirectoryName(loadedPath);
        if (SameDirectory(dir, Path.Combine(exeDir, "gstreamer", "bin"))) return "bundled";
        if (rootPassed != null && SameDirectory(dir, Path.Combine(rootPassed, "bin"))) return "environment";
        return SameDirectory(dir, programFilesBin) ? "programFiles" : "other";
    }

    private static bool SameDirectory(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
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

/// <summary>
/// 試験基盤の 9: アプリに渡した GStreamer まわりの環境。RootPassed は渡した GSTREAMER_1_0_ROOT_MSVC_X86_64 の値
/// （渡していなければ null）、PathGstEntriesPassed は渡した PATH のうち GStreamer のコアの DLL があるフォルダの数。
/// </summary>
internal sealed record AppGstEnvironment(
    bool BundledNextToExe, string? RootPassed, bool RootRemoved, int PathGstEntriesRemoved, int PathGstEntriesPassed)
{
    public string Describe() =>
        $"app-env: bundledGstreamer={(BundledNextToExe ? "yes" : "no")}" +
        $" GSTREAMER_1_0_ROOT_MSVC_X86_64={RootPassed ?? "<not passed>"} (removed={(RootRemoved ? "yes" : "no")})" +
        $" pathGstBinEntries={PathGstEntriesPassed} (removed={PathGstEntriesRemoved})";
}
