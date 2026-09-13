using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// V3 計測専用: LTC を連続送出したままシークバーで再生位置を 0.5 秒ずらし、
/// SyncDecisionEngine が Seek を決めて戻る経路を 20 回以上発生させる。
/// TIMECODE_SEEK_RESYNC_REPORT_DIR を設定したときだけ動く（通常の E2E には影響しない）。
/// 製品の挙動は変えず、テストから UI を操作するだけ。
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "SeekResync")]
[Collection("E2E")]
public sealed class SyncSeekResyncE2ETests
{
    private const int DefaultCycles = 26;
    private const double DisplaceSeconds = 0.5;
    private const double CycleSeconds = 1.3;

    [SkippableFact]
    public async Task CableLoop_DisplacesPlayhead_AndLetsSyncEngineRecover()
    {
        string? report = Environment.GetEnvironmentVariable("TIMECODE_SEEK_RESYNC_REPORT_DIR");
        Skip.If(string.IsNullOrWhiteSpace(report), "Set TIMECODE_SEEK_RESYNC_REPORT_DIR to enable the resync measurement.");
        report = Path.GetFullPath(report!);
        Directory.CreateDirectory(report);
        using var journal = new MonkeyJournal(Path.Combine(report, "harness.jsonl"), 0);
        journal.Write("prerequisites");
        var prerequisites = E2EAppRunner.ResolvePrereqs();
        Assert.True(prerequisites.SkipReason == null, prerequisites.SkipReason);
        Assert.True(LtcSignalPlayer.TryCreateCablePlayer(out var signal, out var reason), reason);
        using var signalOwner = signal!;
        AccuracyFixture fixture = await AccuracyVideoFixture.CreateAsync(report, step => journal.Write(step));

        int cycles = int.TryParse(Environment.GetEnvironmentVariable("TIMECODE_SEEK_RESYNC_CYCLES"), out int parsed)
            ? Math.Clamp(parsed, 5, 120)
            : DefaultCycles;

        using var phases = new StreamWriter(new FileStream(Path.Combine(report, "phases.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        using var app = E2EAppRunner.Start(prerequisites.ExePath, $"--load-project \"{fixture.ProjectPath}\"",
            Path.Combine(report, "settings.json"));
        MonkeyJson.WriteAppProcessMarker(Path.Combine(report, "app-process.json"), app.Process);
        try
        {
            ConfigureSync(app);
            if (app.Button("BtnPlay").Name == "▶") app.Button("BtnPlay").Invoke();

            signalOwner.Play(new LtcTimecode(0, 0, 0, 0, false), 25,
                TimeSpan.FromSeconds(cycles * CycleSeconds + 3.0));
            await Task.Delay(TimeSpan.FromSeconds(2.5));

            var clock = Stopwatch.StartNew();
            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                Assert.False(app.Process.HasExited, "Measurement app exited during the resync sweep.");
                double current = app.Slider("SeekBar").Value;
                double target = Math.Max(0.5, current - DisplaceSeconds);
                string name = $"resync-{cycle:D2}";
                phases.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "phase-start", name, mode = "freeze",
                    ticks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency,
                    startSeconds = target, durationSeconds = CycleSeconds,
                }, MonkeyJson.Options));
                journal.Write("phase-start", details: new { name, current, target });
                app.Slider("SeekBar").Patterns.RangeValue.Pattern.SetValue(target);
                await Task.Delay(TimeSpan.FromSeconds(CycleSeconds));
                phases.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "phase-end", name, ticks = Stopwatch.GetTimestamp(),
                }, MonkeyJson.Options));
                journal.Write("phase-end", details: new { name, elapsedSeconds = clock.Elapsed.TotalSeconds });
            }

            signalOwner.Stop();
            await Task.Delay(200);
            phases.WriteLine(JsonSerializer.Serialize(new { type = "completed", ticks = Stopwatch.GetTimestamp() }, MonkeyJson.Options));
            Assert.True(app.ExitNormally(TimeSpan.FromSeconds(15)), "App did not flush and exit in fifteen seconds.");
            Assert.Equal(0, app.Process.ExitCode);
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
            if (!app.Process.HasExited)
            {
                try { app.ExitNormally(TimeSpan.FromSeconds(10)); }
                catch (Exception) { /* Dispose が所有 PID を kill する */ }
            }
        }
    }

    private static void ConfigureSync(E2EAppRunner app)
    {
        app.Button("BtnRefreshLtcDevices").Invoke();
        var devices = app.Combo("LtcDeviceCombo");
        int index = Array.FindIndex(devices.Items, item => item.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
        Assert.True(index >= 0, "CABLE Output not listed.");
        devices.Select(index);
        app.Combo("LtcFpsModeCombo").Select(2); // Fixed 25 fps.
        app.Combo("LtcSignalLossModeCombo").Select(0);
        app.Button("BtnStartLtc").Invoke();
        app.Combo("SyncModeCombo").Select(1); // Continue
        app.Combo("GapBehaviorCombo").Select(0);
        if (!app.Button("BtnToggleSync").Name.Contains("ON", StringComparison.OrdinalIgnoreCase))
            app.Button("BtnToggleSync").Invoke();
    }
}
