using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// 段階 5.4 の E2E（終了ダイアログ）。
///  - × → ExitDialog 表示、Enter（既定ボタン）→ キャンセルで継続（プロセス生存・TimeLabel 進行）
///  - BtnExitNormal → exit 0・プロセス残存なし・ログに手順 5 行
///  - Spout ON・全画面中に BtnExitForce → 3 秒以内に exit 2
/// 前提 DLL / 動画 / ディスプレイが欠ける環境ではスキップする。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class ExitDialogE2ETests
{
    private static string NewTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ShippingSettingsPath(string workDir) =>
        Path.Combine(workDir, "settings.json");

    private static double ParsePositionSeconds(string timeLabelText)
    {
        string current = timeLabelText.Split('/')[0].Trim();
        string[] parts = current.Split(':');
        if (parts.Length < 3) return 0;
        return int.Parse(parts[0]) * 3600
             + int.Parse(parts[1]) * 60
             + int.Parse(parts[2]);
    }

    private static string NewestLogPath(string exeDir)
    {
        string logDir = Path.Combine(exeDir, "logs");
        DirectoryInfo di = new(logDir);
        if (!di.Exists) return Path.Combine(logDir, "timecodesyncplayer-.log");
        FileInfo? newest = di.GetFiles("timecodesyncplayer-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        return newest?.FullName ?? Path.Combine(logDir, "timecodesyncplayer-.log");
    }

    private static string ReadLogFrom(string logPath, long offset)
    {
        if (!File.Exists(logPath)) return "";
        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (offset > fs.Length) offset = 0;
        fs.Seek(offset, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private static Button ExitButton(Window dialog, string automationId)
        => dialog.FindFirstDescendant(cf => cf.ByAutomationId(automationId)).AsButton();

    [SkippableFact(Timeout = 120_000)]
    public async Task CloseRequest_ShowsDialog_EnterCancelsAndPlaybackContinues()
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");

        string workDir = NewTempDir("tcs-exit-e2e-enter");
        E2EAppRunner? runner = null;
        try
        {
            string settingsPath = ShippingSettingsPath(workDir);
            string media = TestVideoFactory.GetOrCreate();
            runner = E2EAppRunner.Start(exePath, $"--open \"{media}\"", settingsPath, pausePlaybackIfNeeded: false);

            Button play = runner.Button("BtnPlay");
            if (play.Name != "⏸")
            {
                play.Invoke();
                E2EAssert.WaitUntil(() => play.Name == "⏸", TimeSpan.FromSeconds(3));
            }

            runner.RequestMainWindowClose().Should().BeTrue();
            Window dialog = runner.WaitForTopLevelWindow("ExitDialog", TimeSpan.FromSeconds(10));

            string before = runner.Text("TimeLabel");
            PressEnter(dialog);

            E2EAssert.WaitUntil(
                () => runner.FindTopLevelWindow("ExitDialog") == null,
                TimeSpan.FromSeconds(5));
            runner.Process.HasExited.Should().BeFalse("キャンセル後もプロセスは生存する");

            await Task.Delay(1500);
            string after = runner.Text("TimeLabel");
            ParsePositionSeconds(after).Should().BeGreaterThan(ParsePositionSeconds(before),
                "ダイアログ表示中とキャンセル後も再生が続く");
        }
        finally
        {
            runner?.Dispose();
            TryDeleteDir(workDir);
        }
    }

    [SkippableFact(Timeout = 120_000)]
    public void NormalExit_ExitsZero_AndLogsFiveSteps()
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");

        string workDir = NewTempDir("tcs-exit-e2e-normal");
        E2EAppRunner? runner = null;
        try
        {
            string settingsPath = ShippingSettingsPath(workDir);
            string media = TestVideoFactory.GetOrCreate();
            string exeDir = Path.GetDirectoryName(exePath)!;
            string logPath = NewestLogPath(exeDir);
            long logOffset = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;

            runner = E2EAppRunner.Start(exePath, $"--open \"{media}\"", settingsPath, pausePlaybackIfNeeded: true);
            E2EAssert.WaitUntil(() => runner.Text("TimeLabel").Contains('/'), TimeSpan.FromSeconds(10));

            runner.RequestMainWindowClose().Should().BeTrue();
            Window dialog = runner.WaitForTopLevelWindow("ExitDialog", TimeSpan.FromSeconds(10));
            ExitButton(dialog, "BtnExitNormal").Invoke();

            runner.Process.WaitForExit(30_000).Should().BeTrue("通常終了は 30 秒以内にプロセスが終わる");
            runner.Process.ExitCode.Should().Be(0);

            string log = ReadLogFrom(logPath, logOffset);
            foreach (string step in MainWindowResourceDisposer.StepNames)
                log.Should().Contain(step, $"ログに終了手順 '{step}' が記録される");
        }
        finally
        {
            runner?.Dispose();
            TryDeleteDir(workDir);
        }
    }

    [SkippableFact(Timeout = 120_000)]
    public async Task ForceExit_WhileFullscreenAndSpout_ExitsWithCodeTwo()
    {
        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");

        string workDir = NewTempDir("tcs-exit-e2e-force");
        E2EAppRunner? runner = null;
        try
        {
            string settingsPath = ShippingSettingsPath(workDir);
            string media = TestVideoFactory.GetOrCreate();
            runner = E2EAppRunner.Start(exePath, $"--open \"{media}\"", settingsPath, pausePlaybackIfNeeded: false);

            Button spout = runner.Button("BtnSpout");
            Skip.If(!spout.IsEnabled, "Spout 出力が利用できない");
            if (!spout.Name.Contains("ON", StringComparison.Ordinal))
            {
                spout.Invoke();
                E2EAssert.WaitUntil(() => runner.Button("BtnSpout").Name.Contains("ON", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5));
            }

            E2EAssert.WaitUntil(() => runner.Button("BtnFullscreen").IsEnabled, TimeSpan.FromSeconds(8));
            runner.Button("BtnFullscreen").Invoke();
            E2EAssert.WaitUntil(() => runner.FindTopLevelWindow("FullscreenOutputWindow") != null,
                TimeSpan.FromSeconds(5));
            await Task.Delay(1000);

            runner.RequestMainWindowClose().Should().BeTrue();
            Window dialog = runner.WaitForTopLevelWindow("ExitDialog", TimeSpan.FromSeconds(10));
            var stopwatch = Stopwatch.StartNew();
            ExitButton(dialog, "BtnExitForce").Invoke();

            runner.Process.WaitForExit(3_000).Should().BeTrue("強制終了は 3 秒以内にプロセスが終わる");
            runner.Process.ExitCode.Should().Be(2);
            stopwatch.Stop();
        }
        finally
        {
            runner?.Dispose();
            TryDeleteDir(workDir);
        }
    }

    // Enter（既定ボタン）。SendInput はフォアグラウンド制約を受けるため、ダイアログ HWND へ直接送る。
    private static void PressEnter(Window dialog)
    {
        IntPtr hwnd = dialog.Properties.NativeWindowHandle.Value;
        if (hwnd == IntPtr.Zero)
            throw new SkipException("ExitDialog の HWND を取得できない");
        PostMessage(hwnd, WindowMessageKeyDown, EnterVirtualKey, EnterKeyDownLParam);
        PostMessage(hwnd, WindowMessageKeyUp, EnterVirtualKey, EnterKeyUpLParam);
    }

    private const int WindowMessageKeyDown = 0x0100;
    private const int WindowMessageKeyUp = 0x0101;
    private static readonly IntPtr EnterVirtualKey = new(0x0D);
    private static readonly IntPtr EnterKeyDownLParam = new(0x001C0001);
    private static readonly IntPtr EnterKeyUpLParam = new(unchecked((long)0xC01C0001));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
