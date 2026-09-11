using System.Diagnostics;
using System.Globalization;
using System.IO;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.E2E;

[Trait("Category", "E2E")]
[Trait("Category", "Monkey")]
[Collection("E2E")]
public sealed class MonkeyStressE2ETests
{
    [SkippableFact]
    public async Task SeededCableLoop_RecoversFromMixedOperations_AndClosesCleanly()
    {
        MonkeyTestConfiguration configuration = MonkeyTestConfiguration.FromCurrentEnvironment();
        Skip.IfNot(configuration.IsEnabled, "Use scripts/run-timecodesyncplayer-monkey.ps1 to enable the hardware stress test.");
        Directory.CreateDirectory(configuration.ReportDirectory);
        using var journal = new MonkeyJournal(Path.Combine(configuration.ReportDirectory, "monkey.jsonl"), configuration.Seed);
        var summary = new MonkeyRunSummary
        {
            Seed = configuration.Seed,
            RequestedActions = configuration.ActionCount,
            StartedUtc = DateTimeOffset.UtcNow.ToString("O"),
        };
        E2EAppRunner? app = null;
        LtcSignalPlayer? signal = null;
        try
        {
            journal.Write("prerequisites-start");
            var prerequisites = E2EAppRunner.ResolvePrereqs();
            Assert.True(prerequisites.SkipReason == null, prerequisites.SkipReason);
            Assert.NotNull(LtcSignalPlayer.FindCableCaptureDeviceName());
            Assert.True(LtcSignalPlayer.TryCreateCablePlayer(out signal, out string? reason), reason);
            journal.Write("media-start");
            // Project loading intentionally rejects media outside its own directory.
            string first = Path.Combine(configuration.ReportDirectory, "clip-a.mp4");
            string second = Path.Combine(configuration.ReportDirectory, "clip-b.mp4");
            File.Copy(TestVideoFactory.GetOrCreate(), first, overwrite: true);
            File.Copy(TestVideoFactory.GetOrCreateVariant("monkey"), second, overwrite: true);
            string projectPath = Path.Combine(configuration.ReportDirectory, "fixture.tsp");
            var playlist = new PlaylistState();
            playlist.Tracks.Add(CreateTrack(first, "monkey-a", 0));
            playlist.Tracks.Add(CreateTrack(second, "monkey-b", 25));
            playlist.Select(0);
            await ProjectSerializer.SaveAsync(projectPath, playlist, SyncMode.Single, GapBehavior.Black,
                new CanvasData { Width = 1920, Height = 1080, DefaultFit = "fit-height" });
            journal.Write("app-start", details: new { prerequisites.ExePath, first, second, signal!.SampleRate, signal.Channels });
            app = E2EAppRunner.Start(prerequisites.ExePath, $"--load-project \"{projectPath}\"");
            summary.ProcessId = app.Process.Id;
            MonkeyJson.WriteAppProcessMarker(Path.Combine(configuration.ReportDirectory, "app-process.json"), app.Process);
            journal.Write("configure-start", process: app.Process);
            Wait(app, () => app.Text("CurrentTrackLabel").StartsWith("1/2", StringComparison.Ordinal));
            app.Button("BtnRefreshLtcDevices").Invoke();
            var devices = app.Combo("LtcDeviceCombo");
            int cableIndex = Array.FindIndex(devices.Items, item => item.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
            Assert.True(cableIndex >= 0, "CABLE Output was not listed in app capture devices.");
            devices.Select(cableIndex);
            app.Combo("LtcFpsModeCombo").Select(2); // fixed 25 fps
            app.Button("BtnStartLtc").Invoke();
            PlaySignal(signal, 5);
            summary.InitialLtc = WaitForLtcProgress(app, 5);
            journal.Write("initial-ltc-passed", details: new { summary.InitialLtc });
            journal.Write("gap-preflight-start");
            app.Combo("SyncModeCombo").Select(1);
            SetSync(app, true);
            foreach (int gapMode in new[] { 0, 1 })
            {
                app.Combo("GapBehaviorCombo").Select(gapMode);
                PlaySignal(signal, 21);
                string label = gapMode == 0 ? "Gap: Black" : "Gap: Freeze";
                Wait(app, () => app.Text("CurrentTrackLabel").Contains(label, StringComparison.Ordinal));
                journal.Write("gap-preflight-passed", details: new { label });
            }
            SetSync(app, false);
            app.Combo("SyncModeCombo").Select(0);
            PlaySignal(signal, 5);
            Sample(app.Process, journal, summary);

            foreach (MonkeyOperation operation in MonkeyOperationGenerator.Generate(configuration.Seed, configuration.ActionCount))
            {
                summary.FailedActionIndex = operation.Index;
                summary.AttemptedActions++;
                journal.Write("action-start", operation.Index, operation.Kind, operation);
                EnsureAlive(app);
                bool executed = Execute(app, signal, operation);
                // A UI Automation read after each action must complete. The external watchdog
                // bounds even a provider call which never returns to this test thread.
                string playback = app.Text("TimeLabel");
                string timecode = app.Text("LtcTimecodeText");
                EnsureAlive(app);
                summary.CompletedActions++;
                if (executed) summary.ExecutedActions++;
                else summary.UnavailableActions++;
                journal.Write("action-end", operation.Index, operation.Kind, new { executed, playback, timecode, track = app.Text("CurrentTrackLabel") });
                if (operation.Index % 20 == 0) Sample(app.Process, journal, summary);
                Thread.Sleep(20);
            }

            summary.FailedActionIndex = null;
            journal.Write("recovery-start", process: app.Process);
            SetSync(app, false);
            app.Combo("SyncModeCombo").Select(0);
            app.Combo("LtcSignalLossModeCombo").Select(0);
            if (app.Button("BtnStartLtc").IsEnabled) app.Button("BtnStartLtc").Invoke();
            app.Combo("LtcFpsModeCombo").Select(2);
            PlaySignal(signal, 3);
            summary.FinalLtc = WaitForLtcProgress(app, 3);
            journal.Write("ltc-recovery-passed", details: new { summary.FinalLtc });

            // Ensure a known track and an actual seek, then observe clock advancement.
            if (app.Button("BtnPreviousTrack").IsEnabled) app.Button("BtnPreviousTrack").Invoke();
            Wait(app, () => app.Text("CurrentTrackLabel").StartsWith("1/2", StringComparison.Ordinal));
            app.Slider("SeekBar").Patterns.RangeValue.Pattern.SetValue(0.1);
            Thread.Sleep(200); // Let a player tick replace the immediate slider preview.
            Wait(app, () => ReadPlaybackSeconds(app) is >= 1 and <= 4);
            if (app.Button("BtnPlay").Name == "▶") app.Button("BtnPlay").Invoke();
            double playbackBefore = ReadPlaybackSeconds(app);
            summary.InitialPlayback = app.Text("TimeLabel");
            Wait(app, () => ReadPlaybackSeconds(app) > playbackBefore + 0.25 && ReadPlaybackSeconds(app) < playbackBefore + 3);
            summary.FinalPlayback = app.Text("TimeLabel");
            journal.Write("playback-recovery-passed", details: new { summary.InitialPlayback, summary.FinalPlayback });

            // Also recover sync, not just independent input and manual playback.
            PlaySignal(signal, 10);
            SetSync(app, true);
            _ = WaitForLtcProgress(app, 10);
            Wait(app, () =>
            {
                double ltc = ParseTime(app.Text("LtcTimecodeText"), 25);
                return ltc >= 10 && Math.Abs(ReadPlaybackSeconds(app) - ltc) < 1.0;
            });
            journal.Write("sync-recovery-passed", details: new { playback = app.Text("TimeLabel"), ltc = app.Text("LtcTimecodeText") });
            Sample(app.Process, journal, summary);

            journal.Write("close-start", process: app.Process);
            // Do not stop signal/monitor/render first: this exercises teardown under load.
            Assert.True(app.Process.CloseMainWindow(), "Failed to request graceful app shutdown.");
            Assert.True(app.Process.WaitForExit(10000), "App did not close within 10 seconds while receiving LTC.");
            summary.ExitCode = app.Process.ExitCode;
            Assert.Equal(0, summary.ExitCode);
            summary.Success = true;
            journal.Write("close-passed", process: app.Process);
        }
        catch (Exception exception)
        {
            summary.FailureType = exception.GetType().FullName;
            summary.FailureMessage = exception.ToString();
            journal.Write("failure", summary.FailedActionIndex, details: new { exception = exception.ToString() }, process: app?.Process);
            throw;
        }
        finally
        {
            summary.EndedUtc = DateTimeOffset.UtcNow.ToString("O");
            summary.ElapsedMilliseconds = journal.ElapsedMilliseconds;
            MonkeyJson.WriteSummary(Path.Combine(configuration.ReportDirectory, "summary.json"), summary);
            // Save diagnostic evidence before disposing UIA or WASAPI, which can themselves hang.
            journal.Write("cleanup-start");
            try { signal?.Dispose(); }
            finally { app?.Dispose(); }
            journal.Write("cleanup-complete");
        }
    }

    private static bool Execute(E2EAppRunner app, LtcSignalPlayer signal, MonkeyOperation operation)
    {
        switch (operation.Kind)
        {
            case MonkeyOperationKind.TogglePlayback: return InvokeIfEnabled(app, "BtnPlay");
            case MonkeyOperationKind.PreviousTrack: return InvokeIfEnabled(app, "BtnPreviousTrack");
            case MonkeyOperationKind.NextTrack: return InvokeIfEnabled(app, "BtnNextTrack");
            case MonkeyOperationKind.ToggleSync: return InvokeIfEnabled(app, "BtnToggleSync");
            case MonkeyOperationKind.ToggleMonitor:
                return InvokeIfEnabled(app, app.Button("BtnStopLtc").IsEnabled ? "BtnStopLtc" : "BtnStartLtc");
            case MonkeyOperationKind.SeekBurst:
                var slider = app.Slider("SeekBar");
                if (!slider.IsEnabled) return false;
                var range = slider.Patterns.RangeValue.Pattern;
                if (range.IsReadOnly) return false;
                foreach (double ratio in operation.SeekRatios)
                    range.SetValue(range.Minimum + (range.Maximum - range.Minimum) * ratio);
                return true;
            case MonkeyOperationKind.SelectSyncMode: return SelectIfEnabled(app, "SyncModeCombo", operation.SyncModeIndex);
            case MonkeyOperationKind.SelectGapBehavior: return SelectIfEnabled(app, "GapBehaviorCombo", operation.GapBehaviorIndex);
            case MonkeyOperationKind.SelectSignalLossMode: return SelectIfEnabled(app, "LtcSignalLossModeCombo", operation.SignalLossModeIndex);
            case MonkeyOperationKind.StopSignal:
                signal.Stop();
                Thread.Sleep(600); // Include actual signal-loss detection, as well as fast restarts.
                return true;
            case MonkeyOperationKind.RestartSignal: PlaySignal(signal, 0); return true;
            case MonkeyOperationKind.JumpSignal: PlaySignal(signal, operation.SignalStartSeconds); return true;
            case MonkeyOperationKind.NoisySignal:
                PlaySignal(signal, operation.SignalStartSeconds, new LtcTestSignalGenerator.Options
                {
                    NoiseSeed = operation.NoiseSeed,
                    NoiseAmplitude = operation.NoiseAmplitude,
                    Amplitude = 0.2f,
                });
                return true;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static PlaylistTrack CreateTrack(string path, string name, int offset) => new(
        Guid.NewGuid(), path, name, TimeSpan.Zero, null,
        TimeSpan.FromSeconds(offset), TimeSpan.FromSeconds(20), TimeSpan.Zero, 30, true);

    private static bool InvokeIfEnabled(E2EAppRunner app, string id)
    {
        var button = app.Button(id);
        if (!button.IsEnabled) return false;
        button.Invoke();
        return true;
    }

    private static bool SelectIfEnabled(E2EAppRunner app, string id, int index)
    {
        var combo = app.Combo(id);
        if (!combo.IsEnabled) return false;
        combo.Select(index);
        return true;
    }

    private static void SetSync(E2EAppRunner app, bool enabled)
    {
        var button = app.Button("BtnToggleSync");
        if (button.Name.Contains("ON", StringComparison.OrdinalIgnoreCase) != enabled) button.Invoke();
        Wait(app, () => button.Name.Contains("ON", StringComparison.OrdinalIgnoreCase) == enabled);
    }

    private static void PlaySignal(LtcSignalPlayer signal, int seconds, LtcTestSignalGenerator.Options? options = null) =>
        signal.Play(new LtcTimecode(0, 0, seconds, 0, false), 25, TimeSpan.FromSeconds(20), options);

    private static string WaitForLtcProgress(E2EAppRunner app, int startSeconds)
    {
        double? first = null;
        string observed = "";
        Wait(app, () =>
        {
            observed = app.Text("LtcTimecodeText");
            double current = ParseTime(observed, 25);
            if (current < startSeconds || current >= startSeconds + 8) return false;
            first ??= current;
            return current >= first + 0.2;
        });
        return observed;
    }

    private static double ReadPlaybackSeconds(E2EAppRunner app) => ParseTime(app.Text("TimeLabel").Split('/')[0].Trim(), 30);

    private static double ParseTime(string value, int fps)
    {
        string[] parts = value.Split(':');
        if (parts.Length != 4 || !parts.All(p => double.TryParse(p, NumberStyles.Number, CultureInfo.InvariantCulture, out _))) return -1;
        double[] values = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return values[0] * 3600 + values[1] * 60 + values[2] + values[3] / fps;
    }

    private static void EnsureAlive(E2EAppRunner app)
    {
        app.Process.Refresh();
        Assert.False(app.Process.HasExited, "App process exited during monkey operations.");
    }

    private static void Wait(E2EAppRunner app, Func<bool> condition) => E2EAssert.WaitUntil(() => { EnsureAlive(app); return condition(); }, TimeSpan.FromSeconds(10));

    private static void Sample(Process process, MonkeyJournal journal, MonkeyRunSummary summary)
    {
        process.Refresh();
        summary.SampleCount++;
        summary.PeakPrivateMemoryBytes = Math.Max(summary.PeakPrivateMemoryBytes, process.PrivateMemorySize64);
        summary.PeakHandleCount = Math.Max(summary.PeakHandleCount, process.HandleCount);
        journal.Write("sample", process: process);
    }
}
