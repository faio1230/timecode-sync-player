using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// GStreamer バックエンドの実機 E2E。
///  - 再生 → Spout ON → 別プロセスの受信で GPU 経路フレームが届く
///  - 受信側を終了 → 再起動しても送信が継続する
///  - トラック切り替え反復でアプリが安定し、ロードが全て成功する
///  - 再生中にウィンドウを閉じて正常終了する
/// 前提 (shim DLL / GStreamer ランタイム / テスト動画 / recv ツール) が
/// 欠ける環境ではスキップ。起動した全プロセスは finally で確実に停止する。
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class GStreamerBackendE2ETests
{
    private const string SenderEnvVar = "TIMECODE_SYNC_PLAYER_SPOUT_NAME";

    [SkippableFact(Timeout = 240_000)]
    public void GStreamerBackend_PlaysAndPublishesGpuFramesToSpout()
    {
        (string exePath, string repoRoot) = PrepareEnvironment(out _);
        // 2 回の受信 (各 8 秒) を跨げる長さの素材
        string media = Path.Combine(repoRoot, "artifacts", "media", "test_720p60_long.mp4");
        string recvExe = Path.Combine(
            repoRoot, "native", "gst-shim", "proto", "build-debug", "tcs-gst-proto.exe");
        Skip.If(!File.Exists(media), $"テスト動画が無い: {media}");
        Skip.If(!File.Exists(recvExe), $"recv ツールが無い: {recvExe}");

        string workDir = NewTempDir("tcs-gst-e2e");
        string settingsPath = Path.Combine(workDir, "settings.json");
        File.WriteAllText(settingsPath, "{\"backend\":1}");

        string sender = $"TCSGstE2E-{Environment.ProcessId}-{DateTime.UtcNow.Ticks}";
        Environment.SetEnvironmentVariable(SenderEnvVar, sender);

        E2EAppRunner? runner = null;
        ReceiverRun? recv = null;
        try
        {
            runner = E2EAppRunner.Start(
                exePath, $"--open \"{media}\"", settingsPath, pausePlaybackIfNeeded: false);

            // --open は再生状態で読み込まれる (LoadFile → pause=no)。
            Button spout = Button(runner, "BtnSpout");
            E2EAssert.WaitUntil(() => spout.IsEnabled, TimeSpan.FromSeconds(10));
            spout.Invoke();
            E2EAssert.WaitUntil(
                () => runner.Text("BtnSpout")?.Contains("ON", StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(10));

            // 1 回目の受信
            string recvDir1 = NewTempDir("tcs-gst-e2e-recv1");
            recv = StartReceiver(recvExe, sender, 8, Path.Combine(recvDir1, "frame"));
            recv.Process.WaitForExit(40_000);
            recv.Process.WaitForExit(); // 非同期 stdout 回収の完了待ち
            string recvLog1 = recv.Log;
            recvLog1.Should().Contain("rc=0", "1 回目の Spout 受信が成功すべき");
            Directory.GetFiles(recvDir1, "frame_*.bmp").Length.Should()
                .BeGreaterThanOrEqualTo(3, $"recv1 log:\n{recvLog1}");

            // 受信側を終了させて再起動しても送信が継続する
            E2EAppRunner.KillProcess(recv.Process);
            recv.Process.Dispose();
            recv = null;
            Thread.Sleep(500);

            DateTime recv2StartedAt = DateTime.Now;
            string recvDir2 = NewTempDir("tcs-gst-e2e-recv2");
            recv = StartReceiver(recvExe, sender, 8, Path.Combine(recvDir2, "frame"));
            recv.Process.WaitForExit(40_000);
            recv.Process.WaitForExit();
            string recvLog2 = recv.Log;
            // 注: Spout 受信の 2 個目プロセスは SDK 側のフレーム同期都合で
            // コピー画像が更新されないことがある (proto 送信では再起動後の
            // 内容更新を実測済み)。ここでは接続とフレームイベント、および
            // 受信 #2 の時間帯にアプリが描画・Spout 公開を続けたことを検証する。
            recvLog2.Should().MatchRegex(@"got=[1-9]\d*\s+new=[1-9]\d*\s+rc=0",
                $"受信側再起動後も接続・受信できるべき。recv2 log:\n{recvLog2}");
            (string? lastPerfLine, DateTime? lastPerfAt) = LastPerfLine(exePath);
            lastPerfAt.Should().NotBeNull("Playback perf ログが存在する");
            lastPerfAt!.Value.Should().BeAfter(recv2StartedAt.AddSeconds(-3),
                "受信側再起動中もアプリが描画・公開を継続している");
            lastPerfLine!.Should().Contain("spoutEnabled=true");

            // GPU デコーダが選択されたことがアプリログに残る
            string appLog = WaitForLog(exePath, "プレイヤー生成", TimeSpan.FromSeconds(10));
            appLog.Should().Contain("GstBackendState: プレイヤー生成");
            appLog.Should().MatchRegex(@"FetchMetadata: 1280x720 .*V:d3d11h264dec");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SenderEnvVar, null);
            E2EAppRunner.KillProcess(recv?.Process);
            recv?.Process.Dispose();
            runner?.Dispose();
            TryDeleteDir(workDir);
        }
    }

    [SkippableFact(Timeout = 240_000)]
    public void GStreamerBackend_SurvivesRepeatedTrackSwitches()
    {
        (string exePath, string repoRoot) = PrepareEnvironment(out _);
        string[] files =
        [
            Path.Combine(repoRoot, "artifacts", "media", "test_1080p60.mp4"),
            Path.Combine(repoRoot, "artifacts", "media", "test_720p25.mkv"),
            Path.Combine(repoRoot, "artifacts", "media", "test_720p25.avi"),
            Path.Combine(repoRoot, "artifacts", "media", "test_720p50.ts"),
        ];
        foreach (string f in files)
            Skip.If(!File.Exists(f), $"テスト動画が無い: {f}");

        string workDir = NewTempDir("tcs-gst-e2e-switch");
        string settingsPath = Path.Combine(workDir, "settings.json");
        File.WriteAllText(settingsPath, "{\"backend\":1}");

        E2EAppRunner? runner = null;
        try
        {
            string args = $"--open \"{files[0]}\" --playlist \"{files[1]}\" \"{files[2]}\" \"{files[3]}\"";
            runner = E2EAppRunner.Start(exePath, args, settingsPath, pausePlaybackIfNeeded: false);

            int loadsBefore = CountInLog(exePath, "LoadFile path=");
            int failuresBefore = CountInLog(exePath, "loadfile 失敗");

            Button next = Button(runner, "BtnNextTrack");
            Button prev = Button(runner, "BtnPreviousTrack");
            for (int i = 0; i < 3; i++)
            {
                next.Invoke();
                Thread.Sleep(700);
            }
            for (int i = 0; i < 3; i++)
            {
                prev.Invoke();
                Thread.Sleep(700);
            }

            E2EAssert.WaitUntil(
                () => CountInLog(exePath, "LoadFile path=") >= loadsBefore + 7,
                TimeSpan.FromSeconds(20));

            runner.Process.HasExited.Should().BeFalse("切り替え反復後もアプリは動作継続している");
            CountInLog(exePath, "LoadFile path=").Should().BeGreaterThanOrEqualTo(loadsBefore + 7);
            CountInLog(exePath, "loadfile 失敗").Should().Be(failuresBefore,
                "全トラックのロードが成功している");
        }
        finally
        {
            runner?.Dispose();
            TryDeleteDir(workDir);
        }
    }

    [SkippableFact(Timeout = 180_000)]
    public void GStreamerBackend_ClosesGracefullyDuringPlayback()
    {
        (string exePath, string repoRoot) = PrepareEnvironment(out _);
        string media = Path.Combine(repoRoot, "artifacts", "media", "test_1080p60.mp4");
        Skip.If(!File.Exists(media), $"テスト動画が無い: {media}");

        string workDir = NewTempDir("tcs-gst-e2e-close");
        string settingsPath = Path.Combine(workDir, "settings.json");
        File.WriteAllText(settingsPath, "{\"backend\":1}");

        E2EAppRunner? runner = null;
        try
        {
            runner = E2EAppRunner.Start(
                exePath, $"--open \"{media}\"", settingsPath, pausePlaybackIfNeeded: false);

            WaitForLog(exePath, "first frame displayed", TimeSpan.FromSeconds(15));
            Thread.Sleep(1000); // 再生が数フレーム進む

            runner.MainWindow.Close();
            // 段階 5.1: × は終了確認ダイアログを経由する。通常終了で exit 0。
            Window exitDialog = runner.WaitForTopLevelWindow("ExitDialog", TimeSpan.FromSeconds(10));
            exitDialog.FindFirstDescendant(cf => cf.ByAutomationId("BtnExitNormal")).AsButton().Invoke();
            bool exited = runner.Process.WaitForExit(20_000);
            exited.Should().BeTrue("再生中のクローズでアプリが正常終了する");
            runner.Process.ExitCode.Should().Be(0);
        }
        finally
        {
            runner?.Dispose();
            TryDeleteDir(workDir);
        }
    }

    // ---- helpers ----

    private static (string ExePath, string RepoRoot) PrepareEnvironment(out string exeDir)
    {
        var (exePath, skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");
        exeDir = Path.GetDirectoryName(exePath)!;
        Skip.If(!File.Exists(Path.Combine(exeDir, "tcs_gstreamer.dll")),
            "tcs_gstreamer.dll が出力に無い (build-shim 未実行)");
        Skip.If(!GstRuntimePresent(), "GStreamer ランタイムが見つからない");
        return (exePath, FindRepoRoot());
    }

    private sealed class ReceiverRun
    {
        public Process Process = null!;
        public System.Text.StringBuilder Output { get; } = new();
        public string Log => Output.ToString();
    }

    /// <summary>
    /// recv ツールを起動し stdout を非同期で回収する。
    /// (WaitForExit 後に ReadToEnd するとパイプ満杯で相手がブロックし
    ///  相互デッドロックになるため。)
    /// </summary>
    private static ReceiverRun StartReceiver(string recvExe, string sender, int seconds, string prefix)
    {
        var psi = new ProcessStartInfo
        {
            FileName = recvExe,
            Arguments = $"recv {sender} {seconds} \"{prefix}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        psi.Environment["PATH"] = GstBinDirectory() + ";" + psi.Environment["PATH"];
        var run = new ReceiverRun { Process = Process.Start(psi)! };
        run.Process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                lock (run.Output) run.Output.AppendLine(e.Data);
        };
        run.Process.BeginOutputReadLine();
        return run;
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

    private static string WaitForLog(string exePath, string needle, TimeSpan timeout)
    {
        string exeDir = Path.GetDirectoryName(exePath)!;
        DateTime deadline = DateTime.UtcNow + timeout;
        string text = "";
        while (DateTime.UtcNow < deadline)
        {
            text = ReadNewestLog(exeDir);
            if (text.Contains(needle, StringComparison.Ordinal)) return text;
            Thread.Sleep(400);
        }
        text.Should().Contain(needle, $"アプリログに '{needle}' が記録されるはず");
        return text;
    }

    private static int CountInLog(string exePath, string needle)
    {
        string exeDir = Path.GetDirectoryName(exePath)!;
        string text = ReadNewestLog(exeDir);
        return Regex.Matches(text, Regex.Escape(needle)).Count;
    }

    private static string ReadNewestLog(string exeDir)
    {
        string logDir = Path.Combine(exeDir, "logs");
        DirectoryInfo di = new(logDir);
        if (!di.Exists) return "";
        FileInfo? newest = di.GetFiles("timecodesyncplayer-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        if (newest is null) return "";
        using var fs = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    private static string TailOfLog(string exePath, int lines)
    {
        string text = ReadNewestLog(Path.GetDirectoryName(exePath)!);
        string[] all = text.Split('\n');
        return string.Join('\n', all.Skip(Math.Max(0, all.Length - lines)));
    }

    /// <summary>最新の Playback perf 行とそのタイムスタンプ (ローカル時刻)。</summary>
    private static (string? Line, DateTime? At) LastPerfLine(string exePath)
    {
        string text = ReadNewestLog(Path.GetDirectoryName(exePath)!);
        string? last = null;
        DateTime? at = null;
        foreach (string line in text.Split('\n'))
        {
            if (!line.Contains("Playback perf", StringComparison.Ordinal)) continue;
            last = line;
            Match m = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)");
            if (m.Success &&
                DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime ts))
                at = ts;
        }
        return (last, at);
    }

    private static string NewTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
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
