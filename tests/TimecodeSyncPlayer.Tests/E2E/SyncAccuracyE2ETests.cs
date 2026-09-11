using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.E2E;

[Trait("Category", "E2E")]
[Trait("Category", "Accuracy")]
[Collection("E2E")]
public sealed class SyncAccuracyE2ETests
{
    [SkippableFact]
    public async Task CableLoop_RecordsDecodedLtcAndPublishedFramesAcrossSixPhases()
    {
        string? report = Environment.GetEnvironmentVariable("TIMECODE_ACCURACY_REPORT_DIR");
        Skip.If(string.IsNullOrWhiteSpace(report), "Set TIMECODE_ACCURACY_REPORT_DIR to enable sync accuracy measurement.");
        report = Path.GetFullPath(report!);
        Directory.CreateDirectory(report);
        using var journal = new MonkeyJournal(Path.Combine(report, "harness.jsonl"), 0);
        journal.Write("prerequisites");
        var prerequisites = E2EAppRunner.ResolvePrereqs();
        Assert.True(prerequisites.SkipReason == null, prerequisites.SkipReason);
        Assert.True(LtcSignalPlayer.TryCreateCablePlayer(out var signal, out var reason), reason);
        using var signalOwner = signal!;
        AccuracyFixture fixture = await AccuracyVideoFixture.CreateAsync(report, step => journal.Write(step));
        string tracePath = Path.Combine(report, "trace.jsonl");
        Assert.False(File.Exists(tracePath), "A new trace output path is required.");
        using var phases = new StreamWriter(new FileStream(Path.Combine(report, "phases.jsonl"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        using var app = E2EAppRunner.Start(prerequisites.ExePath, $"--load-project \"{fixture.ProjectPath}\"",
            Path.Combine(report, "settings.json"), environment: new Dictionary<string, string?>
            {
                ["TIMECODE_ACCURACY_TRACE"] = tracePath,
            });
        MonkeyJson.WriteAppProcessMarker(Path.Combine(report, "app-process.json"), app.Process);
        try
        {
            ConfigureSync(app);
            if (app.Button("BtnPlay").Name == "▶") app.Button("BtnPlay").Invoke();
            foreach (var phase in new[]
            {
                (Name: "black-sweep", Mode: "black", Start: 0, Duration: 35),
                (Name: "freeze-sweep", Mode: "freeze", Start: 0, Duration: 35),
                (Name: "seek-a", Mode: "freeze", Start: 3, Duration: 5),
                (Name: "seek-b", Mode: "freeze", Start: 15, Duration: 5),
                (Name: "seek-c", Mode: "freeze", Start: 27, Duration: 5),
                (Name: "seek-back", Mode: "freeze", Start: 3, Duration: 5),
            })
            {
                signalOwner.Stop();
                // The previous phase has ended; drain audio before changing mode/phase ownership.
                await Task.Delay(300);
                app.Combo("GapBehaviorCombo").Select(phase.Mode == "black" ? 0 : 1);
                phases.WriteLine(JsonSerializer.Serialize(new { type = "phase-start", name = phase.Name,
                    mode = phase.Mode, ticks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency,
                    startSeconds = phase.Start, durationSeconds = phase.Duration }, MonkeyJson.Options));
                journal.Write("phase-start", details: new { phase.Name, phase.Start, phase.Duration });
                signalOwner.Play(new LtcTimecode(0, 0, phase.Start, 0, false), 25, TimeSpan.FromSeconds(phase.Duration));
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(phase.Duration + 0.5))
                {
                    await Task.Delay(1000);
                    Assert.False(app.Process.HasExited, "Measurement app exited during the phase.");
                    journal.Write("heartbeat", details: new { phase.Name, elapsedSeconds = clock.Elapsed.TotalSeconds });
                }
                signalOwner.Stop();
                // Include the final delivered input from the hardware buffer in this phase.
                await Task.Delay(200);
                phases.WriteLine(JsonSerializer.Serialize(new { type = "phase-end", name = phase.Name,
                    ticks = Stopwatch.GetTimestamp() }, MonkeyJson.Options));
                journal.Write("phase-end", details: new { phase.Name });
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
            phases.WriteLine(JsonSerializer.Serialize(new { type = "completed", ticks = Stopwatch.GetTimestamp() }, MonkeyJson.Options));
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
        app.Combo("SyncModeCombo").Select(1);
        app.Combo("GapBehaviorCombo").Select(0);
        if (!app.Button("BtnToggleSync").Name.Contains("ON", StringComparison.OrdinalIgnoreCase))
            app.Button("BtnToggleSync").Invoke();
    }
}
