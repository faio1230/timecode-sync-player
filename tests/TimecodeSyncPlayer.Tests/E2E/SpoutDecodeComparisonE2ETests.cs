using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.E2E;

/// <summary>
/// Opt-in capture driver. Run once per decoder with a fresh report directory and the
/// selected app binary; the external receiver and resource sampler are owned by the caller.
/// This test records observations, not an assertion of receiver accuracy or decoder choice.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "SpoutComparison")]
[Collection("E2E")]
public sealed class SpoutDecodeComparisonE2ETests
{
    private const int LtcFps = 25;
    private sealed record Phase(string Name, string Mode, double StartSeconds, double DurationSeconds);

    [SkippableFact]
    public async Task CableLoop_RecordsSpoutEnabledDecoderComparison()
    {
        string? report = Environment.GetEnvironmentVariable("TIMECODE_SPOUT_COMPARE_REPORT_DIR");
        Skip.If(string.IsNullOrWhiteSpace(report), "Set TIMECODE_SPOUT_COMPARE_REPORT_DIR to enable this measurement.");
        report = Path.GetFullPath(report!);
        Directory.CreateDirectory(report);
        string tracePath = Path.Combine(report, "trace.jsonl");
        string phasesPath = Path.Combine(report, "phases.jsonl");
        string manifestPath = Path.Combine(report, "manifest.json");
        foreach (string path in new[] { tracePath, phasesPath, manifestPath, Path.Combine(report, "harness.jsonl") })
            Assert.False(File.Exists(path), $"Fresh capture output required: {path}");
        using var journal = new MonkeyJournal(Path.Combine(report, "harness.jsonl"), 0);

        // ResolvePrereqs normally falls back when an override is invalid. An explicit
        // comparison binary must never silently fall back to a different build.
        string? binaryOverride = Environment.GetEnvironmentVariable("TIMECODE_SYNC_PLAYER_E2E_APP_PATH");
        if (!string.IsNullOrWhiteSpace(binaryOverride))
            Assert.True(File.Exists(binaryOverride), $"App override does not exist: {binaryOverride}");
        var prerequisites = E2EAppRunner.ResolvePrereqs();
        Assert.Null(prerequisites.SkipReason);
        string appPath = Path.GetFullPath(prerequisites.ExePath);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(appPath)!, "SpoutDX.dll")), "SpoutDX.dll is required.");

        string? suppliedProject = Environment.GetEnvironmentVariable("TIMECODE_SPOUT_COMPARE_PROJECT");
        bool generatedFixture = string.IsNullOrWhiteSpace(suppliedProject);
        bool markerExpected = generatedFixture || Environment.GetEnvironmentVariable("TIMECODE_SPOUT_COMPARE_MARKER_FIXTURE") == "1";
        string projectPath = generatedFixture
            ? (await AccuracyVideoFixture.CreateAsync(report, step => journal.Write(step))).ProjectPath
            : Path.GetFullPath(suppliedProject!);
        Assert.True(File.Exists(projectPath), $"Project does not exist: {projectPath}");
        string projectHash = HashFile(projectPath);
        ProjectData? project = await ProjectSerializer.LoadAsync(projectPath);
        Assert.NotNull(project);
        foreach (TrackData track in project!.Tracks.Where(track => track.IsEnabled))
            Assert.True(File.Exists(track.FilePath), $"Enabled source clip does not resolve: {track.Id} {track.FilePath}");
        var playlist = new PlaylistState();
        ProjectSerializer.ApplyToPlaylist(project, playlist);
        Assert.NotEmpty(playlist.Tracks.Where(track => track.IsEnabled));
        Phase[] phasePlan = markerExpected ? MarkerPhases() : SourcePhases(playlist);

        Assert.True(LtcSignalPlayer.TryCreateCablePlayer(out var signal, out var reason), reason);
        using var signalOwner = signal!;
        using var phases = new StreamWriter(new FileStream(phasesPath, FileMode.CreateNew,
            FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        using var app = E2EAppRunner.Start(appPath, $"--load-project \"{projectPath}\"",
            Path.Combine(report, "settings.json"), environment: new Dictionary<string, string?>
            {
                ["TIMECODE_ACCURACY_TRACE"] = tracePath,
            });
        MonkeyJson.WriteAppProcessMarker(Path.Combine(report, "app-process.json"), app.Process);
        try
        {
            ConfigureSyncAndSpout(app);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                schema = 1,
                captureId = Guid.NewGuid(),
                createdUtc = DateTimeOffset.UtcNow,
                qpcFrequency = Stopwatch.Frequency,
                appPath,
                appSha256 = HashFile(appPath),
                appAssemblySha256 = HashFile(Path.ChangeExtension(appPath, ".dll")),
                processId = app.Process.Id,
                processStartUtc = app.Process.StartTime.ToUniversalTime(),
                decoderLabel = Environment.GetEnvironmentVariable("TIMECODE_SPOUT_COMPARE_DECODER_LABEL"),
                projectPath,
                projectSha256 = projectHash,
                markerExpected,
                markerNote = markerExpected ? (generatedFixture ? "Generated fixture markers are independently verified."
                    : "Caller supplied an existing marker fixture; its prior verification evidence must accompany this capture.")
                    : "Original media has no injected marker; markerValid=false is expected and is not frame accuracy evidence.",
                ltcFps = LtcFps,
                inputDevice = app.Combo("LtcDeviceCombo").SelectedItem?.Name,
                syncMode = "Continue",
                syncEnabled = true,
                spoutEnabled = true,
                spoutSender = SpoutDefaults.DefaultSenderName,
                tracePath,
                phasesPath,
                settingsPath = Path.Combine(report, "settings.json"),
                receiverOwnership = "external caller",
                resourceSamplerOwnership = "external caller",
                tracks = playlist.Tracks.Select(track => new
                {
                    track.Id, track.Name, track.FilePath, track.MediaIn, track.MediaOut,
                    track.MediaDuration, track.TimelineOffset, track.SyncOffset, track.FrameRate,
                    track.IsEnabled, fileLength = new FileInfo(track.FilePath).Length,
                    fileLastWriteUtc = File.GetLastWriteTimeUtc(track.FilePath),
                }),
                phasePlan,
            }, MonkeyJson.Options), new UTF8Encoding(false));

            if (app.Button("BtnPlay").Name == "▶") app.Button("BtnPlay").Invoke();
            foreach (Phase phase in phasePlan)
            {
                signalOwner.Stop();
                await Task.Delay(300);
                app.Combo("GapBehaviorCombo").Select(phase.Mode == "black" ? 0 : 1);
                var expected = playlist.FindTrackAtTimelinePosition(phase.StartSeconds);
                phases.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "phase-start", name = phase.Name, mode = phase.Mode,
                    ticks = Stopwatch.GetTimestamp(), frequency = Stopwatch.Frequency,
                    phase.StartSeconds, phase.DurationSeconds,
                    expectedClipId = expected.Track?.Id,
                    expectedClipPath = expected.Track?.FilePath,
                    expectedSourcePositionSeconds = expected.Track == null ? (double?)null : expected.MediaPositionSeconds,
                    expectedTimelineStatus = expected.Status.ToString(),
                }, MonkeyJson.Options));
                journal.Write("phase-start", details: phase);
                signalOwner.Play(ToTimecode(phase.StartSeconds), LtcFps, TimeSpan.FromSeconds(phase.DurationSeconds));
                var clock = Stopwatch.StartNew();
                while (clock.Elapsed < TimeSpan.FromSeconds(phase.DurationSeconds + 0.5))
                {
                    await Task.Delay(1000);
                    Assert.False(app.Process.HasExited, "Comparison app exited during a phase.");
                    AssertSpoutEnabled(app);
                    journal.Write("heartbeat", details: new { phase.Name, elapsedSeconds = clock.Elapsed.TotalSeconds });
                }
                signalOwner.Stop();
                await Task.Delay(200);
                phases.WriteLine(JsonSerializer.Serialize(new { type = "phase-end", name = phase.Name,
                    ticks = Stopwatch.GetTimestamp() }, MonkeyJson.Options));
            }
            journal.Write("close-targets", details: new
            {
                knownWindowHandle = app.MainWindow.Properties.NativeWindowHandle.Value.ToInt64(),
                processWindowHandle = app.Process.MainWindowHandle.ToInt64(),
                processWindowTitle = app.Process.MainWindowTitle,
            });
            // The process can contain an untitled native window that Process.MainWindowHandle
            // selects. Close the WPF window already identified and exercised by UIA.
            Assert.True(app.RequestMainWindowClose(), "Could not post close to the known WPF window.");
            Assert.True(app.Process.WaitForExit(10000), "App did not flush and exit within ten seconds.");
            Assert.Equal(0, app.Process.ExitCode);
            Assert.True(File.Exists(tracePath), "App produced no accuracy trace.");
            using (JsonDocument end = JsonDocument.Parse(File.ReadLines(tracePath).Last()))
            {
                Assert.Equal("end", end.RootElement.GetProperty("type").GetString());
                Assert.Equal(0, end.RootElement.GetProperty("dropped").GetInt64());
                Assert.Equal(0, end.RootElement.GetProperty("errors").GetInt64());
            }
            int ltcEvents = 0;
            int frameEvents = 0;
            foreach (string line in File.ReadLines(tracePath))
            {
                using JsonDocument entry = JsonDocument.Parse(line);
                switch (entry.RootElement.GetProperty("type").GetString())
                {
                    case "ltc": ltcEvents++; break;
                    case "frame": frameEvents++; break;
                }
            }
            Assert.True(ltcEvents > 0, "No LTC was decoded from the cable input.");
            Assert.True(frameEvents > 0, "No bitmap publication was captured.");
            Assert.Equal(projectHash, HashFile(projectPath));
            phases.WriteLine(JsonSerializer.Serialize(new { type = "completed", ticks = Stopwatch.GetTimestamp() }, MonkeyJson.Options));
            journal.Write("passed", details: new { ltcEvents, frameEvents });
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
                app.RequestMainWindowClose();
                app.Process.WaitForExit(10000);
            }
        }
    }

    private static Phase[] MarkerPhases() =>
    [
        new("black-sweep", "black", 0, 35),
        new("freeze-sweep", "freeze", 0, 35),
        new("seek-a", "freeze", 3, 5),
        new("seek-b", "freeze", 15, 5),
        new("seek-c", "freeze", 27, 5),
        new("seek-back", "freeze", 3, 5),
    ];

    private static Phase[] SourcePhases(PlaylistState playlist)
    {
        var tracks = playlist.Tracks.Where(track => track.IsEnabled).ToArray();
        Assert.True(tracks.Length >= 3, "Source comparison requires at least three enabled clips.");
        double continuousDuration = ReadDuration("TIMECODE_SPOUT_COMPARE_CONTINUOUS_SECONDS", 15);
        double seekDuration = ReadDuration("TIMECODE_SPOUT_COMPARE_SEEK_SECONDS", 5);
        Phase At(string name, PlaylistTrack track, double relative, double duration)
        {
            double start = Math.Ceiling((track.TimelineOffset.TotalSeconds + relative) * LtcFps) / LtcFps;
            // Account for overlaps using the saved playlist order. Never silently test
            // a different clip from the one named by this phase.
            Assert.Equal(track.Id, playlist.FindTrackAtTimelinePosition(start).Track?.Id);
            Assert.Equal(track.Id, playlist.FindTrackAtTimelinePosition(start + duration).Track?.Id);
            Assert.InRange(start, 0, 24 * 3600 - duration - 1);
            return new Phase(name, "freeze", start, duration);
        }
        return
        [
            At("continuous-first", tracks[0], 2, continuousDuration),
            At("seek-first-forward", tracks[0], tracks[0].GetEffectiveDuration().TotalSeconds * 0.25, seekDuration),
            At("seek-first-far", tracks[0], tracks[0].GetEffectiveDuration().TotalSeconds * 0.65, seekDuration),
            At("seek-second", tracks[1], tracks[1].GetEffectiveDuration().TotalSeconds * 0.25, seekDuration),
            At("seek-third", tracks[2], tracks[2].GetEffectiveDuration().TotalSeconds * 0.25, seekDuration),
            At("seek-first-back", tracks[0], 2, seekDuration),
        ];
    }

    private static double ReadDuration(string name, double fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        Assert.True(double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double result), $"Invalid {name}.");
        Assert.InRange(result, 1, 120);
        return result;
    }

    private static LtcTimecode ToTimecode(double seconds)
    {
        int frames = checked((int)Math.Round(seconds * LtcFps));
        return new LtcTimecode(frames / (LtcFps * 3600), frames / (LtcFps * 60) % 60,
            frames / LtcFps % 60, frames % LtcFps, false);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ConfigureSyncAndSpout(E2EAppRunner app)
    {
        app.Button("BtnRefreshLtcDevices").Invoke();
        var devices = app.Combo("LtcDeviceCombo");
        int index = Array.FindIndex(devices.Items, item => item.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
        Assert.True(index >= 0, "CABLE Output is not listed.");
        devices.Select(index);
        app.Combo("LtcFpsModeCombo").Select(2);
        app.Combo("LtcSignalLossModeCombo").Select(0);
        app.Button("BtnStartLtc").Invoke();
        app.Combo("SyncModeCombo").Select(1);
        Assert.Contains("25", app.Combo("LtcFpsModeCombo").SelectedItem?.Name ?? "");
        Assert.Contains("Continue", app.Combo("SyncModeCombo").SelectedItem?.Name ?? "");
        if (!app.Button("BtnToggleSync").Name.Contains("ON", StringComparison.OrdinalIgnoreCase))
            app.Button("BtnToggleSync").Invoke();
        Assert.Contains("ON", app.Button("BtnToggleSync").Name);
        Assert.True(app.Button("BtnSpout").IsEnabled, "Spout is unavailable.");
        if (app.Button("BtnSpout").Name != "Spout ON") app.Button("BtnSpout").Invoke();
        E2EAssert.WaitUntil(() => app.Button("BtnSpout").Name == "Spout ON", TimeSpan.FromSeconds(3));
        AssertSpoutEnabled(app);
    }

    private static void AssertSpoutEnabled(E2EAppRunner app)
    {
        Assert.True(app.Button("BtnSpout").IsEnabled, "Spout became unavailable.");
        Assert.Equal("Spout ON", app.Button("BtnSpout").Name);
    }
}
