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

        string sender = $"TCSGstE2E-{Environment.ProcessId}-{DateTime.UtcNow.Ticks}";
        Environment.SetEnvironmentVariable(SenderEnvVar, sender);

        // 受信側は 1 フレームずつ無圧縮 BMP を書き出す (720p60 8 秒で約 3 GB)。
        // 過去に消し忘れでディスクを使い切り E2E 全体が落ちたため、
        // 開始時に旧世代を掃除し、finally で必ず消す。
        PruneStaleTempDirs("tcs-gst-e2e-recv1");
        PruneStaleTempDirs("tcs-gst-e2e-recv2");
        string? recvDir1 = null;
        string? recvDir2 = null;

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
            recvDir1 = NewTempDir("tcs-gst-e2e-recv1");
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
            recvDir2 = NewTempDir("tcs-gst-e2e-recv2");
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
            DateTime runStartedLocal = runner.Process.StartTime;
            List<(DateTime At, long Published)> samples = GpuPublishSamples(exePath)
                .Where(sample => sample.At >= runStartedLocal)
                .ToList();
            samples.Should().NotBeEmpty("Playback perf ログが存在する");
            samples.Should().Contain(sample => sample.At <= recv2StartedAt,
                "受信側再起動の前にも Playback perf 行がある");
            (DateTime At, long Published) beforeRestart = samples.Last(sample => sample.At <= recv2StartedAt);
            (DateTime At, long Published) afterRestart = samples[^1];
            afterRestart.At.Should().BeAfter(recv2StartedAt.AddSeconds(-3),
                "受信側再起動中もアプリが描画・公開を継続している");
            afterRestart.Published.Should().BeGreaterThan(beforeRestart.Published,
                "受信側の再起動をまたいで GPU 公開フレーム数が増えている");

            // GPU デコーダが選択されたことがアプリログに残る
            string appLog = WaitForLog(exePath, "プレイヤー生成", runStartedLocal, TimeSpan.FromSeconds(10));
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
            if (recvDir1 is not null) { TryDeleteDir(recvDir1); }
            if (recvDir2 is not null) { TryDeleteDir(recvDir2); }
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

        E2EAppRunner? runner = null;
        try
        {
            string args = $"--open \"{files[0]}\" --playlist \"{files[1]}\" \"{files[2]}\" \"{files[3]}\"";
            runner = E2EAppRunner.Start(exePath, args, settingsPath, pausePlaybackIfNeeded: false);

            // ログは run をまたいで追記されるため、この run の開始時刻以降の行だけを見る（過去 run を拾わない）。
            DateTime runStartedLocal = runner.Process.StartTime;
            int failuresBefore = CountInLog(exePath, "load 失敗", runStartedLocal);

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

            // 判定は決定的に: 切替で読まれたトラックの並び 1,2,3,2,1,0 をこの run のログ行だけで確認する。
            // 先頭の 0（初回ロード）は開始状態なので期待に含めない。
            int[] expectedIndices = [1, 2, 3, 2, 1, 0];
            List<int> actualIndices = LoadedTrackIndices(exePath, runStartedLocal);
            DateTime sequenceDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!ContainsInOrder(actualIndices, expectedIndices) && DateTime.UtcNow < sequenceDeadline)
            {
                Thread.Sleep(200);
                actualIndices = LoadedTrackIndices(exePath, runStartedLocal);
            }

            runner.Process.HasExited.Should().BeFalse("切り替え反復後もアプリは動作継続している");
            ContainsInOrder(actualIndices, expectedIndices).Should().BeTrue(
                $"期待するロード順 [{string.Join(",", expectedIndices)}] に対して実際は [{string.Join(",", actualIndices)}]");
            CountInLog(exePath, "load 失敗", runStartedLocal).Should().Be(failuresBefore,
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

        E2EAppRunner? runner = null;
        try
        {
            runner = E2EAppRunner.Start(
                exePath, $"--open \"{media}\"", settingsPath, pausePlaybackIfNeeded: false);

            // 出荷構成（GStreamer + Gpu）の公開フレーム数で再生開始を待つ。
            // 「first frame displayed」は段 3 で消えたため使わない。
            DateTime runStartedLocal = runner.Process.StartTime;
            WaitForGpuPublishedFrame(exePath, runStartedLocal, TimeSpan.FromSeconds(15));
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

    /// <summary>
    /// sinceLocal 以降の行だけを返す。ログは run をまたいで追記されるため、
    /// 過去 run の同じ文言を拾って偽合格しないようにする。
    /// </summary>
    private static IEnumerable<string> LinesSince(string text, DateTime sinceLocal)
    {
        foreach (string line in text.Split('\n'))
        {
            Match t = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)");
            if (!t.Success ||
                !DateTime.TryParse(t.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime at) ||
                at < sinceLocal)
                continue;
            yield return line;
        }
    }

    private static string WaitForLog(string exePath, string needle, DateTime sinceLocal, TimeSpan timeout)
    {
        string exeDir = Path.GetDirectoryName(exePath)!;
        DateTime deadline = DateTime.UtcNow + timeout;
        string scoped = "";
        while (DateTime.UtcNow < deadline)
        {
            string text = ReadNewestLog(exeDir);
            scoped = string.Join('\n', LinesSince(text, sinceLocal));
            if (scoped.Contains(needle, StringComparison.Ordinal)) return scoped;
            Thread.Sleep(400);
        }
        scoped.Should().Contain(needle, $"この run のアプリログに '{needle}' が記録されるはず");
        return scoped;
    }

    private static int CountInLog(string exePath, string needle, DateTime sinceLocal)
    {
        string text = ReadNewestLog(Path.GetDirectoryName(exePath)!);
        return LinesSince(text, sinceLocal).Sum(line => Regex.Matches(line, Regex.Escape(needle)).Count);
    }

    /// <summary>
    /// この run の Playback perf 行で gpuPublishedFrames が 0 を超えるまで待つ。
    /// 「first frame displayed」は段 3（CPU 合成の除去）で製品から消えたため、
    /// 出荷構成（GStreamer + Gpu）の公開フレーム数で再生開始を判定する。
    /// </summary>
    private static void WaitForGpuPublishedFrame(string exePath, DateTime sinceLocal, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        long published = 0;
        while (DateTime.UtcNow < deadline)
        {
            published = GpuPublishSamples(exePath)
                .Where(sample => sample.At >= sinceLocal)
                .Select(sample => sample.Published)
                .DefaultIfEmpty(0)
                .Max();
            if (published > 0) return;
            Thread.Sleep(400);
        }
        published.Should().BeGreaterThan(0,
            "この run で GPU 公開フレーム数が 0 を超えている（再生が始まっている）");
    }

    /// <summary>
    /// sinceLocal 以降のログ行だけから「Playlist track loaded index=」の並び（読み込み順）を取る。
    /// ログは run をまたいで追記されるため、過去 run の並びを判定に使わない。
    /// </summary>
    private static List<int> LoadedTrackIndices(string exePath, DateTime sinceLocal)
    {
        string text = ReadNewestLog(Path.GetDirectoryName(exePath)!);
        var indices = new List<int>();
        foreach (string line in text.Split('\n'))
        {
            if (!line.Contains("Playlist track loaded index=", StringComparison.Ordinal)) continue;
            Match t = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)");
            if (!t.Success ||
                !DateTime.TryParse(t.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime at) ||
                at < sinceLocal)
                continue;
            Match m = Regex.Match(line, @"Playlist track loaded index=(\d+)");
            if (m.Success) indices.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        return indices;
    }

    /// <summary>expected が actual に順序どおり（隣接は問わない）現れるか。</summary>
    private static bool ContainsInOrder(List<int> actual, int[] expected)
    {
        int searchFrom = 0;
        foreach (int value in expected)
        {
            int found = actual.IndexOf(value, searchFrom);
            if (found < 0) return false;
            searchFrom = found + 1;
        }
        return true;
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

    /// <summary>Playback perf 行から GPU 合成の公開フレーム数を時系列で取り出す（ローカル時刻）。</summary>
    private static List<(DateTime At, long Published)> GpuPublishSamples(string exePath)
    {
        var samples = new List<(DateTime, long)>();
        foreach (string line in ReadNewestLog(Path.GetDirectoryName(exePath)!).Split('\n'))
        {
            if (!line.Contains("Playback perf", StringComparison.Ordinal)) continue;
            Match timestamp = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d+)");
            Match frames = Regex.Match(line, @"gpuPublishedFrames=(\d+)");
            if (!timestamp.Success || !frames.Success) continue;
            if (DateTime.TryParse(timestamp.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime at))
                samples.Add((at, long.Parse(frames.Groups[1].Value, CultureInfo.InvariantCulture)));
        }
        return samples;
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

    /// <summary>
    /// 過去の実行が残した %TEMP%/&lt;prefix&gt;/&lt;GUID&gt; を消す。
    /// このテストだけが作る名前空間なので他に影響しない。
    /// </summary>
    private static void PruneStaleTempDirs(string prefix)
    {
        string root = Path.Combine(Path.GetTempPath(), prefix);
        if (!Directory.Exists(root)) { return; }
        foreach (string dir in Directory.GetDirectories(root))
        {
            TryDeleteDir(dir);
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 検証用一時なので失敗は無視 */ }
    }
}
