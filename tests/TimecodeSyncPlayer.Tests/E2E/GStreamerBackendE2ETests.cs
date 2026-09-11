using System.Diagnostics;
using System.IO;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// GStreamer バックエンドの実機 E2E。アプリを backend=Gstreamer で起動し、
/// 再生開始 → Spout ON → 別プロセスの Spout 受信 (tcs-gst-proto recv) で
/// GPU 経路のフレームが実際に届くことを検証する。
/// 前提 (shim DLL / GStreamer ランタイム / テスト動画 / recv ツール) が
/// 欠ける環境ではスキップ。起動した全プロセスは finally で確実に停止する。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class GStreamerBackendE2ETests
{
    private const string SenderEnvVar = "TIMECODE_SYNC_PLAYER_SPOUT_NAME";

    [SkippableFact(Timeout = 180_000)]
    public void GStreamerBackend_PlaysAndPublishesGpuFramesToSpout()
    {
        string repoRoot = FindRepoRoot();
        string media = Path.Combine(repoRoot, "artifacts", "media", "test_1080p60.mp4");
        string recvExe = Path.Combine(
            repoRoot, "native", "gst-shim", "proto", "build-debug", "tcs-gst-proto.exe");

        var (exePath, skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");
        Skip.If(!File.Exists(media), $"テスト動画が無い: {media}");
        Skip.If(!File.Exists(recvExe), $"recv ツールが無い: {recvExe}");

        string exeDir = Path.GetDirectoryName(exePath)!;
        Skip.If(!File.Exists(Path.Combine(exeDir, "tcs_gstreamer.dll")),
            "tcs_gstreamer.dll が出力に無い (build-shim 未実行)");
        Skip.If(!GstRuntimePresent(), "GStreamer ランタイムが見つからない");

        string settingsDir = Path.Combine(Path.GetTempPath(), "tcs-gst-e2e", Guid.NewGuid().ToString("N"));
        string recvDir = Path.Combine(Path.GetTempPath(), "tcs-gst-e2e-recv", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        Directory.CreateDirectory(recvDir);
        string settingsPath = Path.Combine(settingsDir, "settings.json");
        File.WriteAllText(settingsPath, "{\"backend\":1}");

        string sender = $"TCSGstE2E-{Environment.ProcessId}-{DateTime.UtcNow.Ticks}";
        Environment.SetEnvironmentVariable(SenderEnvVar, sender);

        E2EAppRunner? runner = null;
        Process? recv = null;
        try
        {
            runner = E2EAppRunner.Start(
                exePath, $"--open \"{media}\"", settingsPath, pausePlaybackIfNeeded: false);

            // --open は再生状態で読み込まれる (LoadFile → pause=no)。
            // ここでは Spout ON だけ切り替えて受信を待つ。
            Button spout = Button(runner, "BtnSpout");
            E2EAssert.WaitUntil(() => spout.IsEnabled, TimeSpan.FromSeconds(10));
            spout.Invoke();
            E2EAssert.WaitUntil(
                () => runner.Text("BtnSpout")?.Contains("ON", StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(10));

            string prefix = Path.Combine(recvDir, "frame");
            var psi = new ProcessStartInfo
            {
                FileName = recvExe,
                Arguments = $"recv {sender} 8 {prefix}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            psi.Environment["PATH"] = GstBinDirectory() + ";" + psi.Environment["PATH"];
            recv = Process.Start(psi)!;
            recv.WaitForExit(40_000);
            string recvLog = recv.StandardOutput.ReadToEnd();

            recv.ExitCode.Should().Be(0, $"Spout 受信が成功すべきだった。recv log:\n{recvLog}");
            Directory.GetFiles(recvDir, "frame_*.bmp").Length.Should().BeGreaterThanOrEqualTo(3);

            // 再生が進み、GPU デコーダが選択されたことがアプリログに残る
            // (shim 側の stderr "loaded ..." はテストホスト側に出るため
            //  Serilog ログからは FetchMetadata の V: フィールドで検証する)
            string appLog = ReadNewestAppLog(exeDir);
            appLog.Should().Contain("GstBackendState: プレイヤー生成");
            appLog.Should().MatchRegex(@"FetchMetadata: 1920x1080 .*V:d3d11h264dec");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SenderEnvVar, null);
            E2EAppRunner.KillProcess(recv);
            recv?.Dispose();
            runner?.Dispose();
            TryDeleteDir(recvDir);
            TryDeleteDir(settingsDir);
        }
    }

    private static Button Button(E2EAppRunner runner, string automationId)
    {
        var el = runner.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        el.Should().NotBeNull($"automationId {automationId} が見つかるはず");
        return el.AsButton();
    }

    private static bool GstRuntimePresent() => GstBinDirectory() is not null;

    private static string? GstBinDirectory()
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

    private static string ReadNewestAppLog(string exeDir)
    {
        string logDir = Path.Combine(exeDir, "logs");
        DirectoryInfo di = new(logDir);
        if (!di.Exists) return "";
        FileInfo newest = di.GetFiles("timecodesyncplayer-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault() ?? throw new FileNotFoundException("ログが無い");
        // Serilog は 1 秒刻み flush。書き込み中のファイルは共有読み込みする。
        for (int i = 0; i < 20; i++)
        {
            string text = ReadShared(newest.FullName);
            if (text.Contains("プレイヤー生成")) return text;
            Thread.Sleep(500);
        }
        return ReadShared(newest.FullName);
    }

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TimecodeSyncPlayer.slnx")))
            dir = dir.Parent;
        dir.Should().NotBeNull("テストは worktree 内のビルド出力から実行される");
        return dir!.FullName;
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 検証用一時なので失敗は無視 */ }
    }
}
