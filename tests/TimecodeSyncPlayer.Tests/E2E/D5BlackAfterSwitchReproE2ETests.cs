using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using TimecodeSyncPlayer.Output;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// D5 の決定的な再現系。GStreamer × GPU 合成で、トラック切替（世代変更）の直後に
/// 映像が黒のまま戻らない事象を、**Spout 受信（アプリの A1 読み戻しとは独立した消費者）**
/// が受け取った画素で判定する。出力トレース（TIMECODE_SYNC_PLAYER_OUTPUT_TRACE）も
/// 同時に取り、取得側の状態（acquire の imageId が固まっていないか）を残す。
///
///  - 対照: 切替しなければ受信フレームは映像のまま（観測経路の妥当性）
///  - 再現: 再生中のトラック切替で、切替後の受信フレームが黒のままになる（D5）
/// </summary>
[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class D5BlackAfterSwitchReproE2ETests
{
    private const string SenderEnvVar = "TIMECODE_SYNC_PLAYER_SPOUT_NAME";

    [SkippableFact(Timeout = 180_000)]
    public void GpuCompositor_WithoutTrackSwitch_PublishesContent()
    {
        Observation observation = RunSpoutObservation(receiverSeconds: 12, switchAtMs: 0);

        observation.Frames.Should().NotBeEmpty("Spout 受信でフレームが保存されるはず");
        observation.Frames.Last().BlackRatio.Should().BeLessThan(0.9,
            $"切替が無ければ映像が続くはず。受信フレーム: {Describe(observation.Frames)}");
    }

    [SkippableFact(Timeout = 180_000)]
    public void GpuCompositor_TrackSwitchWithoutHook_PublishesContentAfterSwitch()
    {
        // フック無しの切替は健全（受信フレームが映像のまま）。
        Observation observation = RunSpoutObservation(receiverSeconds: 18, switchAtMs: 10_500);

        observation.Frames.Should().NotBeEmpty("Spout 受信でフレームが保存されるはず");
        observation.Frames.Last().BlackRatio.Should().BeLessThan(0.9,
            $"切替単独では黒にならないはず。受信フレーム: {Describe(observation.Frames)}");
    }

    [SkippableFact(Timeout = 180_000)]
    public void GpuCompositor_ForcedBlackAtSwitch_PublishesContentAfterSwitch()
    {
        // D5 の決定的再現: 世代切替からその世代の最初のフレーム取得まで gap を Black に
        // 固定する（TCS_TEST_FORCE_GAP_BLACK_ON_SWITCH=1）。修正前は lease が返らず、
        // 切替後の出力が黒のままになる。
        Observation observation = RunSpoutObservation(
            receiverSeconds: 18, switchAtMs: 10_500, forceGapBlackOnSwitch: true);

        observation.Frames.Should().NotBeEmpty("Spout 受信でフレームが保存されるはず");
        observation.Frames.Last().BlackRatio.Should().BeLessThan(0.9,
            $"Black 強制が終わった後に映像へ戻るはず（黒のままなら表示経路が本当に黒）。受信フレーム: {Describe(observation.Frames)}");
    }

    // ---- helpers ----

    private sealed record Observation(List<(string Name, double BlackRatio)> Frames, string TraceSummary);

    private static string Describe(List<(string Name, double BlackRatio)> frames) =>
        frames.Count == 0 ? "(なし)" : string.Join(", ", frames.Select(f => $"{f.Name}={f.BlackRatio:F2}"));

    private static Observation RunSpoutObservation(double receiverSeconds, double switchAtMs,
        bool forceGapBlackOnSwitch = false)
    {
        (string exePath, string repoRoot) = PrepareEnvironment();
        string media = Path.Combine(repoRoot, "artifacts", "media", "d1-60s.mp4");
        string nextMedia = Path.Combine(repoRoot, "artifacts", "media", "d1-60s.mp4");
        string recvExe = Path.Combine(repoRoot, "native", "gst-shim", "proto", "build-debug", "tcs-gst-proto.exe");
        Skip.If(!File.Exists(media), $"テスト動画が無い: {media}");
        Skip.If(!File.Exists(nextMedia), $"テスト動画が無い: {nextMedia}");
        Skip.If(!File.Exists(recvExe), $"recv ツールが無い: {recvExe}");

        string workDir = NewTempDir("tcs-d5-repro");
        string recvDir = NewTempDir("tcs-d5-recv");
        string settingsPath = Path.Combine(workDir, "settings.json");
        string sender = $"TCSD5-{Environment.ProcessId}-{DateTime.UtcNow.Ticks}";

        E2EAppRunner? runner = null;
        ReceiverRun? recv = null;
        try
        {
            var environment = new Dictionary<string, string?>
            {
                [SenderEnvVar] = sender,
                ["TIMECODE_SYNC_PLAYER_OUTPUT_TRACE"] = workDir,
                [OutputEngine.ForceGapBlackOnSwitchEnvironmentVariable] =
                    forceGapBlackOnSwitch ? "1" : null,
            };
            runner = E2EAppRunner.Start(
                exePath, $"--open \"{media}\" --playlist \"{nextMedia}\"", settingsPath, pausePlaybackIfNeeded: false,
                environment: environment);

            Button spout = Button(runner, "BtnSpout");
            E2EAssert.WaitUntil(() => spout.IsEnabled, TimeSpan.FromSeconds(10));
            spout.Invoke();
            E2EAssert.WaitUntil(
                () => runner.Text("BtnSpout")?.Contains("ON", StringComparison.OrdinalIgnoreCase) == true,
                TimeSpan.FromSeconds(10));

            recv = StartReceiver(recvExe, sender, receiverSeconds, Path.Combine(recvDir, "frame"));

            if (switchAtMs > 0)
            {
                DateTime appStarted = runner.Process.StartTime;
                while ((DateTime.Now - appStarted).TotalMilliseconds < switchAtMs)
                    Thread.Sleep(50);
                Button(runner, "BtnNextTrack").Invoke();
                Console.WriteLine($"[D5] track switch invoked at {(DateTime.Now - appStarted).TotalMilliseconds:F0}ms after app start");
            }

            recv.Process.WaitForExit((int)(receiverSeconds * 1000) + 60_000);
            recv.Process.WaitForExit(); // 非同期 stdout 回収の完了待ち
            string recvLog = recv.Log;
            recvLog.Should().MatchRegex(@"got=[1-9]\d*", $"Spout 受信がフレームを受けるはず。recv log:\n{recvLog}");

            // 出力トレース（events.jsonl）は正常終了時に書かれる。取得側の状態を残すため閉じる。
            if (!runner.Process.HasExited)
                runner.ExitNormally(TimeSpan.FromSeconds(10));
            string tracePath = Path.Combine(workDir, "events.jsonl");
            for (int i = 0; i < 20 && !File.Exists(tracePath); i++)
                Thread.Sleep(250);
            Console.WriteLine($"[D5] exited={runner.Process.HasExited} traceExists={File.Exists(tracePath)}");

            string[] files = Directory.GetFiles(recvDir, "frame_*.bmp")
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            List<(string Name, double BlackRatio)> observed = files
                .Select(path => (Path.GetFileName(path), BlackRatio(path)))
                .ToList();
            string summary = SummarizeOutputTrace(workDir);
            string lastSave = files.Length > 0
                ? File.GetCreationTimeUtc(files[^1]).ToString("HH:mm:ss.fff") : "-";
            Console.WriteLine($"[D5] hook={forceGapBlackOnSwitch} switchAt={switchAtMs} receiverSeconds={receiverSeconds} saved={files.Length} " +
                              $"appStartUtc={runner.Process.StartTime.ToUniversalTime():HH:mm:ss.fff} lastSaveUtc={lastSave} " +
                              $"frames: {Describe(observed)}");
            Console.WriteLine($"[D5] output trace: {summary}");
            return new Observation(observed, summary);
        }
        finally
        {
            E2EAppRunner.KillProcess(recv?.Process);
            recv?.Process.Dispose();
            runner?.Dispose();
            TryDeleteDir(workDir);
            TryDeleteDir(recvDir);
        }
    }

    /// <summary>直近 3 秒（トレース末尾基準）の取得・合成の件数と、取得 imageId の固着を要約する。</summary>
    private static string SummarizeOutputTrace(string workDir)
    {
        string path = Path.Combine(workDir, "events.jsonl");
        if (!File.Exists(path)) return "(events.jsonl なし)";
        var events = new List<(string Stage, long Qpc, string? Detail, long ImageId)>();
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string stage = root.GetProperty("stage").GetString() ?? "";
            string? detail = root.TryGetProperty("detail", out JsonElement d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() : null;
            long imageId = root.TryGetProperty("imageId", out JsonElement i) ? i.GetInt64() : 0;
            events.Add((stage, root.GetProperty("qpc").GetInt64(), detail, imageId));
        }
        if (events.Count == 0) return "(イベント 0)";
        long end = events.Max(e => e.Qpc);
        long from = end - 3L * 10_000_000;
        List<(string Stage, long Qpc, string? Detail, long ImageId)> last = events.Where(e => e.Qpc >= from).ToList();
        int distinctAcquireIds = last
            .Where(e => e.Stage == "compose.acquire" && e.Detail == "Ready")
            .Select(e => e.ImageId).Distinct().Count();
        string generations = string.Join(",", events
            .Where(e => e.Stage == "lifecycle" && e.Detail?.StartsWith("gst.generation", StringComparison.Ordinal) == true)
            .Select(e => e.Detail));
        return $"last3s publish={last.Count(e => e.Stage == "compose.publish")} " +
               $"srv={last.Count(e => e.Stage == "compose.srv")} " +
               $"delivery={last.Count(e => e.Stage == "gst.delivery")} " +
               $"acquireReady={last.Count(e => e.Stage == "compose.acquire" && e.Detail == "Ready")} " +
               $"distinctAcquireIds={distinctAcquireIds} generations=[{generations}]";
    }

    /// <summary>24bit BMP を間引いて読み、ほぼ黒 (RGB 各 &lt; 16) の画素比率を返す。</summary>
    private static double BlackRatio(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        int pixelOffset = BitConverter.ToInt32(data, 10);
        int width = BitConverter.ToInt32(data, 18);
        int height = Math.Abs(BitConverter.ToInt32(data, 22));
        int bytesPerPixel = BitConverter.ToInt16(data, 28) / 8;
        int rowPitch = ((width * bytesPerPixel + 3) / 4) * 4;
        long black = 0, total = 0;
        for (int y = 0; y < height; y += 8)
        {
            for (int x = 0; x < width; x += 8)
            {
                int i = pixelOffset + y * rowPitch + x * bytesPerPixel;
                if (i + 2 >= data.Length || bytesPerPixel < 3) continue;
                total++;
                if (data[i] < 16 && data[i + 1] < 16 && data[i + 2] < 16) black++;
            }
        }
        return total == 0 ? 1.0 : (double)black / total;
    }

    private sealed class ReceiverRun
    {
        public Process Process = null!;
        public System.Text.StringBuilder Output { get; } = new();
        public string Log => Output.ToString();
    }

    private static ReceiverRun StartReceiver(string recvExe, string sender, double seconds, string prefix)
    {
        var psi = new ProcessStartInfo
        {
            FileName = recvExe,
            Arguments = $"recv {sender} {seconds.ToString(CultureInfo.InvariantCulture)} \"{prefix}\"",
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

    private static (string ExePath, string RepoRoot) PrepareEnvironment()
    {
        var (exePath, skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");
        string exeDir = Path.GetDirectoryName(exePath)!;
        Skip.If(!File.Exists(Path.Combine(exeDir, "tcs_gstreamer.dll")),
            "tcs_gstreamer.dll が出力に無い (build-shim 未実行)");
        Skip.If(!GstRuntimePresent(), "GStreamer ランタイムが見つからない");
        return (exePath, FindRepoRoot());
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
