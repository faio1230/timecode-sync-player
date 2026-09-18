using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// M6: ダーティーな LTC を段階的に流し、デコードと同期の限界点を測る。
/// 加工はテスト側（<see cref="DirtyLtcSignal"/>）だけで行い、製品コードは変更しない。
/// 計画は環境変数 TCS_M6_DIRTY_PLAN の JSON、レポート先は TIMECODE_ACCURACY_REPORT_DIR。
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "DirtyLtc")]
[Collection("E2E")]
public sealed class DirtyLtcE2ETests
{
    [SkippableFact]
    public async Task CableLoop_PlaysDegradedLtcLevelsAndRecordsTrace()
    {
        string? planPath = Environment.GetEnvironmentVariable("TCS_M6_DIRTY_PLAN");
        Skip.If(string.IsNullOrWhiteSpace(planPath) || !File.Exists(planPath),
            "Set TCS_M6_DIRTY_PLAN to a dirty-plan json.");
        string? report = Environment.GetEnvironmentVariable("TIMECODE_ACCURACY_REPORT_DIR");
        Skip.If(string.IsNullOrWhiteSpace(report), "Set TIMECODE_ACCURACY_REPORT_DIR to enable the measurement.");
        report = Path.GetFullPath(report!);
        Directory.CreateDirectory(report);

        DirtyLtcPlan plan = DirtyLtcPlan.Load(planPath!);
        File.Copy(planPath!, Path.Combine(report, "dirty-plan.json"), overwrite: true);

        using var journal = new MonkeyJournal(Path.Combine(report, "harness.jsonl"), 0);
        journal.Write("prerequisites");
        journal.Write("dirty-plan", details: new { plan.Condition, plan.LtcFps, levels = plan.Levels.Count });
        var prerequisites = E2EAppRunner.ResolvePrereqs();
        Assert.True(prerequisites.SkipReason == null, prerequisites.SkipReason);
        Assert.True(LtcSignalPlayer.TryCreateCablePlayer(out var signal, out var reason), reason);
        using var signalOwner = signal!;
        AccuracyFixture fixture = await AccuracyVideoFixture.CreateAsync(report, step => journal.Write(step), plan.LtcFps);
        string tracePath = Path.Combine(report, "trace.jsonl");
        Assert.False(File.Exists(tracePath), "A new trace output path is required.");
        using var phases = new StreamWriter(new FileStream(Path.Combine(report, "phases.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        using var app = E2EAppRunner.Start(prerequisites.ExePath, $"--load-project \"{fixture.ProjectPath}\"",
            Path.Combine(report, "settings.json"), environment: new Dictionary<string, string?>
            {
                ["TIMECODE_ACCURACY_TRACE"] = tracePath,
                [SyncAccuracyTrace.ReferenceFpsEnvironmentVariable] = V3LtcFpsMatrix.FormatReferenceFps(plan.LtcFps),
            });
        MonkeyJson.WriteAppProcessMarker(Path.Combine(report, "app-process.json"), app.Process);
        try
        {
            ConfigureSync(app, V3LtcFpsMatrix.ResolveFpsModeIndex(plan.LtcFpsMode, plan.LtcFps));
            if (app.Button("BtnPlay").Name == "▶") app.Button("BtnPlay").Invoke();
            foreach (DirtyLevel level in plan.Levels)
            {
                signalOwner.Stop();
                await Task.Delay(300);
                double playSeconds = level.Seconds + plan.SettlingSeconds;
                int frameCount = Math.Max(1, (int)Math.Ceiling(playSeconds * plan.LtcFps));
                IReadOnlyList<LtcTimecode> timecodes = LtcSignalPlayer.BuildContinuousTimecodes(
                    new LtcTimecode(0, 0, 0, 0, false), plan.LtcFps, frameCount);
                int generatedRate = level.GenerateSampleRate is double rate
                    ? (int)Math.Round(rate)
                    : signalOwner.SampleRate;
                float[] samples = LtcTestSignalGenerator.Generate(timecodes, plan.LtcFps, generatedRate,
                    DirtyLtcSignal.BuildOptions(level));
                samples = DirtyLtcSignal.Process(samples, generatedRate, signalOwner.SampleRate, level);
                phases.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "phase-start",
                    name = $"dirty-{level.Name}",
                    mode = "dirty",
                    ticks = Stopwatch.GetTimestamp(),
                    frequency = Stopwatch.Frequency,
                    startSeconds = 0.0,
                    durationSeconds = playSeconds,
                    settlingSeconds = plan.SettlingSeconds,
                    condition = level,
                }, MonkeyJson.Options));
                journal.Write("phase-start", details: new { level.Name, level.Seconds });
                signalOwner.SendSamples(samples);
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(playSeconds + 0.3))
                {
                    await Task.Delay(1000);
                    Assert.False(app.Process.HasExited, "Measurement app exited during the level.");
                    journal.Write("heartbeat", details: new { level.Name, elapsedSeconds = clock.Elapsed.TotalSeconds });
                }
                signalOwner.Stop();
                await Task.Delay(200);
                phases.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "phase-end",
                    name = $"dirty-{level.Name}",
                    ticks = Stopwatch.GetTimestamp(),
                }, MonkeyJson.Options));
                journal.Write("phase-end", details: new { level.Name });
                await Task.Delay((int)Math.Round(plan.GapSeconds * 1000));
            }
            Assert.True(app.ExitNormally(TimeSpan.FromSeconds(15)), "App did not flush and exit in fifteen seconds.");
            Assert.Equal(0, app.Process.ExitCode);
            Assert.True(File.Exists(tracePath), "App produced no trace.");
            using (JsonDocument end = JsonDocument.Parse(File.ReadLines(tracePath).Last()))
            {
                Assert.Equal("end", end.RootElement.GetProperty("type").GetString());
                Assert.Equal(0, end.RootElement.GetProperty("dropped").GetInt64());
                Assert.Equal(0, end.RootElement.GetProperty("errors").GetInt64());
            }
            phases.WriteLine(JsonSerializer.Serialize(new { type = "completed", ticks = Stopwatch.GetTimestamp() },
                MonkeyJson.Options));
            journal.Write("passed");
        }
        catch (Exception error)
        {
            journal.Write("failure", details: new { error = error.ToString() });
            throw;
        }
        finally
        {
            signalOwner.Stop();
            // Preserve partial traces on failures too; Dispose remains the owned-PID kill fallback.
            if (!app.Process.HasExited)
            {
                try { app.ExitNormally(TimeSpan.FromSeconds(10)); }
                catch (Exception) { /* Dispose が所有 PID を kill する */ }
            }
        }
    }

    private static void ConfigureSync(E2EAppRunner app, int ltcFpsModeIndex)
    {
        app.Button("BtnRefreshLtcDevices").Invoke();
        var devices = app.Combo("LtcDeviceCombo");
        int index = Array.FindIndex(devices.Items, item => item.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
        Assert.True(index >= 0, "CABLE Output not listed.");
        devices.Select(index);
        app.Combo("LtcFpsModeCombo").Select(ltcFpsModeIndex);
        app.Combo("LtcSignalLossModeCombo").Select(0);
        app.Button("BtnStartLtc").Invoke();
        app.Combo("SyncModeCombo").Select(1);
        app.Combo("GapBehaviorCombo").Select(0);
        if (!app.Button("BtnToggleSync").Name.Contains("ON", StringComparison.OrdinalIgnoreCase))
            app.Button("BtnToggleSync").Invoke();
    }
}
