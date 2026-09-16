using System.IO;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// D8 調査用の測定ハーネス。製品の修正はしない。
/// TIMECODE_D8_REPORT_DIR を設定した run だけ動く（通常の E2E には影響しない）。
/// TIMECODE_D8_MODE=change（既定）: 解像度が変わる切替（1080p60 → 720p25）
/// TIMECODE_D8_MODE=same: 解像度が同じ切替（1080p60 → 1080p）
/// 切替の前後を観察し、可能なら通常終了して出力トレースを書き出させる。
/// shim の stderr は TIMECODE_ACCURACY_REPORT_DIR を設定したときに保存される（T10 の仕組み）。
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "D8Measurement")]
[Collection("E2E")]
public sealed class D8DeviceLostMeasurementE2ETests
{
    private const string ReportVariable = "TIMECODE_D8_REPORT_DIR";
    private const string ModeVariable = "TIMECODE_D8_MODE";
    private const string TraceVariable = "TIMECODE_SYNC_PLAYER_OUTPUT_TRACE";
    private const string TraceCapacityVariable = "TIMECODE_SYNC_PLAYER_OUTPUT_TRACE_CAPACITY";
    private const int ObservationSeconds = 60;

    [SkippableFact(Timeout = 300_000)]
    public void TrackSwitch_CapturesDeviceLossTimeline()
    {
        string? report = Environment.GetEnvironmentVariable(ReportVariable);
        Skip.If(string.IsNullOrWhiteSpace(report), $"Set {ReportVariable} to enable the D8 measurement.");
        string mode = Environment.GetEnvironmentVariable(ModeVariable)?.Trim().ToLowerInvariant() switch
        {
            "same" => "same",
            _ => "change",
        };
        report = Path.GetFullPath(report!);

        (string exePath, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(!string.IsNullOrEmpty(skipReason), skipReason ?? "");
        string repoRoot = FindRepoRoot();
        string media = Path.Combine(repoRoot, "artifacts", "media");
        string[] files = mode == "same"
            ? [
                Path.Combine(media, "test_1080p60.mp4"),
                Path.Combine(media, "d1-60s.mp4"),
                Path.Combine(media, "test_1080p60.mp4"),
                Path.Combine(media, "d1-60s.mp4"),
            ]
            : [
                Path.Combine(media, "test_1080p60.mp4"),
                Path.Combine(media, "test_720p25.mkv"),
                Path.Combine(media, "test_720p25.avi"),
                Path.Combine(media, "test_720p50.ts"),
            ];
        foreach (string file in files)
            Skip.If(!File.Exists(file), $"テスト動画が無い: {file}");

        string runDir = Path.Combine(report, $"run-{DateTime.Now:yyyyMMdd-HHmmss}-{mode}");
        Directory.CreateDirectory(runDir);
        using var journal = new MonkeyJournal(Path.Combine(runDir, "harness.jsonl"), 0);

        E2EAppRunner? runner = null;
        try
        {
            var environment = new Dictionary<string, string?>
            {
                [TraceVariable] = Path.Combine(runDir, "trace"),
                [TraceCapacityVariable] = "300000",
            };
            journal.Write("app-start", details: new { mode, files });
            runner = E2EAppRunner.Start(
                exePath, $"--open \"{files[0]}\" --playlist \"{files[1]}\" \"{files[2]}\" \"{files[3]}\"",
                Path.Combine(runDir, "settings.json"), pausePlaybackIfNeeded: false, environment: environment);
            MonkeyJson.WriteAppProcessMarker(Path.Combine(runDir, "app-process.json"), runner.Process);
            journal.Write("app-started", process: runner.Process);

            E2EAssert.WaitUntil(() => runner.Button("BtnNextTrack").IsEnabled, TimeSpan.FromSeconds(15));
            Thread.Sleep(2000);
            journal.Write("switch-start", process: runner.Process);
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    runner.Button("BtnNextTrack").Invoke();
                    journal.Write($"switch-click-{i}", details: new { ok = true }, process: runner.Process);
                }
                catch (Exception ex)
                {
                    journal.Write($"switch-click-{i}", details: new { ok = false, type = ex.GetType().Name, message = ex.Message }, process: runner.Process);
                }
                Thread.Sleep(700);
            }

            for (int i = 0; i < ObservationSeconds / 5; i++)
            {
                Thread.Sleep(5000);
                journal.Write("sample", details: new { seconds = (i + 1) * 5 }, process: runner.Process);
                if (runner.Process.HasExited) break;
            }

            bool exited = runner.ExitNormally(TimeSpan.FromSeconds(20));
            int? exitCode = null;
            try { if (runner.Process.HasExited) exitCode = runner.Process.ExitCode; }
            catch (InvalidOperationException) { }
            journal.Write("exit", details: new { exited, exitCode, runDir });
        }
        finally
        {
            runner?.Dispose();
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TimecodeSyncPlayer.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("リポジトリルートが見つかりません。");
    }
}
