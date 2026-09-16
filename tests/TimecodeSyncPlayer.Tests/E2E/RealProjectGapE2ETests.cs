using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.E2E;

[Trait("Category", "E2E")]
[Trait("Category", "RealProject")]
[Collection("E2E")]
public sealed class RealProjectGapE2ETests
{
    [SkippableFact]
    public async Task OriginalProject_BoundariesAndHeldTimecodeSwitchesFollowCorrectTrack()
    {
        string? path = Environment.GetEnvironmentVariable("TIMECODE_REAL_PROJECT_PATH");
        Skip.If(string.IsNullOrEmpty(path), "Set TIMECODE_REAL_PROJECT_PATH to enable real-media verification.");
        string report = Environment.GetEnvironmentVariable("TIMECODE_REAL_PROJECT_REPORT_DIR")
            ?? throw new InvalidOperationException("TIMECODE_REAL_PROJECT_REPORT_DIR is required.");
        Directory.CreateDirectory(report);
        string originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path!)));
        using var journal = new MonkeyJournal(Path.Combine(report, "real-project.jsonl"), 0);
        var prerequisites = E2EAppRunner.ResolvePrereqs();
        Assert.True(prerequisites.SkipReason == null, prerequisites.SkipReason);
        ProjectData project = (await ProjectSerializer.LoadAsync(path!))!;
        Assert.NotNull(project);
        Assert.NotEmpty(project.Tracks);
        foreach (TrackData track in project.Tracks) Assert.True(File.Exists(track.FilePath), $"Missing media: {track.FilePath}");
        journal.Write("project", details: new { path, originalHash, project.Tracks });
        Assert.True(LtcSignalPlayer.TryCreateCablePlayer(out var signal, out var reason), reason);
        using var signalOwner = signal!;
        using var app = E2EAppRunner.Start(prerequisites.ExePath, $"--load-project \"{path}\"");
        MonkeyJson.WriteAppProcessMarker(Path.Combine(report, "app-process.json"), app.Process);
        try
        {
            ConfigureSync(app);

            // Independent interval expectations from the original project file, retaining row priority.
            // 開始境界の手前は 2 フレーム（25fps で 80ms）を使う。T2 のサンプル時計（既定 on）は
            // フレーム終端からの経過（通常 40〜60ms、上限 0.5s）を同期値に足すため、1 フレーム手前
            // （40ms）では受信時点の実時間が既に境界を越えていることがあり、アプリが次のトラックへ
            // 入るのが正しい挙動になる。境界ちょうどの checkpoint が移行後の確認を兼ねる。
            // 終端側は手前チェックを置かない: 保持 LTC でも age の分だけ実時間が先行し、
            // メディアが EOF に達してギャップへ入る（「まだ現トラック」は安定して観測できない）。
            var checkpoints = new SortedSet<double> { 1 };
            foreach (TrackData track in project.Tracks.Where(t => t.IsEnabled))
            {
                double start = track.TimelineOffset.TotalSeconds;
                double startFrame = Math.Ceiling(start * 25) / 25;
                if (startFrame - 0.08 >= 1) checkpoints.Add(Math.Round(startFrame - 0.08, 2));
                if (startFrame >= 1) checkpoints.Add(startFrame);

                double endFrame = Math.Ceiling(End(track) * 25) / 25;
                if (endFrame >= 1) checkpoints.Add(endFrame);
            }
            foreach (double seconds in checkpoints)
            {
                journal.Write("boundary-start", details: new { seconds, expectedTrack = ExpectedTrack(project, seconds)?.Name ?? "Gap: Black" });
                signalOwner.PlayHeld(seconds, 25, TimeSpan.FromSeconds(15));
                WaitForHeld(app, seconds);
                TrackData? expected = ExpectedTrack(project, seconds);
                Wait(app, () => Matches(app, expected), $"TC {seconds:F2}: expected {expected?.Name ?? "Gap: Black"}");
                if (expected == null) AssertBlackImage(app, report, $"black-{seconds:F2}", journal);
                else
                {
                    double expectedPosition = seconds - expected.TimelineOffset.TotalSeconds + expected.MediaIn.TotalSeconds + expected.SyncOffset.TotalSeconds;
                    Wait(app, () => Math.Abs(PlaybackSeconds(app) - expectedPosition) < 1,
                        $"TC {seconds:F2}: expected media position {expectedPosition:F3}");
                    if (End(expected) - seconds > 2)
                    {
                        signalOwner.Stop();
                        await Task.Delay(350);
                        Wait(app, () => PlaybackSeconds(app) > expectedPosition + 0.3,
                            $"TC {seconds:F2}: playback must pass the last requested seek target");
                        double before = PlaybackSeconds(app);
                        Wait(app, () => PlaybackSeconds(app) > before + 0.15,
                            $"TC {seconds:F2}: native playback must advance without more LTC seeks");
                    }
                }
                journal.Write("boundary-passed", details: new { seconds, track = app.Text("CurrentTrackLabel"), playback = app.Text("TimeLabel") }, process: app.Process);
            }

            // Use the final gap for every project, including fully overlapping playlists.
            double gapTime = Math.Ceiling(project.Tracks.Max(End)) + 2;
            journal.Write("held-switch-start", details: new { gapTime });
            signalOwner.PlayHeld(gapTime, 25, TimeSpan.FromSeconds(60));
            WaitForHeld(app, gapTime);
            signalOwner.Stop();
            await Task.Delay(350); // Drain queued audio; mode reset must not accept another Initial frame.
            Wait(app, () => app.Text("CurrentTrackLabel").Contains("Gap: Black"), "Enter black gap");
            AssertBlackImage(app, report, "held-black-before", journal);
            app.Combo("GapBehaviorCombo").Select(1);
            Wait(app, () => app.Text("CurrentTrackLabel").Contains("Gap: Freeze"), "Black to Freeze with held LTC");
            CaptureImage(app, report, "held-freeze", journal);
            app.Combo("GapBehaviorCombo").Select(0);
            Wait(app, () => app.Text("CurrentTrackLabel").Contains("Gap: Black"), "Freeze to Black with held LTC");
            AssertBlackImage(app, report, "held-black-after", journal);
            app.Combo("SyncModeCombo").Select(0);
            Wait(app, () => !app.Text("CurrentTrackLabel").Contains("Gap:"), "Continue to Single clears gap");
            app.Combo("SyncModeCombo").Select(1);
            Wait(app, () => app.Text("CurrentTrackLabel").Contains("Gap: Black"), "Single to Continue reevaluates held LTC");
            AssertBlackImage(app, report, "held-continue-restored", journal);
            SetSync(app, false);
            SetSync(app, true);
            Wait(app, () => app.Text("CurrentTrackLabel").Contains("Gap: Black"), "Sync ON reevaluates held LTC");
            AssertBlackImage(app, report, "held-sync-restored", journal);
            journal.Write("held-switch-passed");

            double recoverTime = project.Tracks.Where(t => t.IsEnabled).Min(t => t.TimelineOffset.TotalSeconds) + 10;
            TrackData recovery = ExpectedTrack(project, recoverTime)!;
            journal.Write("recovery-start", details: new { recoverTime, recovery.Name });
            int totalFrames = (int)Math.Round(recoverTime * 25);
            signalOwner.Play(new LtcTimecode(totalFrames / 90000, totalFrames / 1500 % 60, totalFrames / 25 % 60, totalFrames % 25, false), 25, TimeSpan.FromSeconds(20));
            Wait(app, () => Matches(app, recovery), "Return to active clip after gap");
            Wait(app, () =>
            {
                double ltc = Seconds(app.Text("LtcTimecodeText"), 25);
                double media = PlaybackSeconds(app);
                double expected = ltc - recovery.TimelineOffset.TotalSeconds + recovery.MediaIn.TotalSeconds + recovery.SyncOffset.TotalSeconds;
                return ltc >= recoverTime && Math.Abs(media - expected) < 1;
            }, "Playback must follow LTC after gap and mode changes");
            CaptureImage(app, report, "recovered-video", journal);
            journal.Write("recovery-passed", details: new { track = app.Text("CurrentTrackLabel"), playback = app.Text("TimeLabel"), ltc = app.Text("LtcTimecodeText") });
            Assert.True(app.ExitNormally(TimeSpan.FromSeconds(15)));
            Assert.Equal(0, app.Process.ExitCode);
            signalOwner.Stop();
            await VerifyVisibleFreeze(prerequisites.ExePath, project, report, signalOwner, journal);
            journal.Write("passed", process: app.Process);
        }
        catch (Exception error)
        {
            journal.Write("failure", details: new { error = error.ToString() }, process: app.Process);
            throw;
        }
        finally
        {
            string finalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path!)));
            journal.Write("source-integrity", details: new { originalHash, finalHash });
            Assert.Equal(originalHash, finalHash);
        }
    }

    private static void ConfigureSync(E2EAppRunner app)
    {
        app.Button("BtnRefreshLtcDevices").Invoke();
        var devices = app.Combo("LtcDeviceCombo");
        int index = Array.FindIndex(devices.Items, item => item.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
        Assert.True(index >= 0, "CABLE Output not listed.");
        devices.Select(index);
        app.Combo("LtcFpsModeCombo").Select(2);
        app.Combo("LtcSignalLossModeCombo").Select(0);
        app.Button("BtnStartLtc").Invoke();
        app.Combo("SyncModeCombo").Select(1);
        app.Combo("GapBehaviorCombo").Select(0);
        SetSync(app, true);
    }

    private static async Task VerifyVisibleFreeze(string exe, ProjectData source, string report,
        LtcSignalPlayer signal, MonkeyJournal journal)
    {
        // The original videos fade to black at EOF. Trim a separate fixture at 10s so
        // black output cannot masquerade as a successfully captured Freeze frame.
        TrackData media = source.Tracks.First(t => t.Name == "Substitute_A");
        string localMedia = Path.Combine(report, "freeze-anchor.mp4");
        File.Copy(media.FilePath, localMedia);
        var playlist = new PlaylistState();
        playlist.Tracks.Add(new PlaylistTrack(media.Id, localMedia, media.Name,
            TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.Zero,
            media.MediaDuration, TimeSpan.Zero, media.FrameRate, true));
        string fixturePath = Path.Combine(report, "visible-freeze.tsp");
        await ProjectSerializer.SaveAsync(fixturePath, playlist, SyncMode.Continue, GapBehavior.Black,
            new CanvasData { Width = 1920, Height = 1080, DefaultFit = "fit-height" });
        using var app = E2EAppRunner.Start(exe, $"--load-project \"{fixturePath}\"");
        MonkeyJson.WriteAppProcessMarker(Path.Combine(report, "app-process.json"), app.Process);
        journal.Write("visible-freeze-start", process: app.Process);
        ConfigureSync(app);
        signal.PlayHeld(12, 25, TimeSpan.FromSeconds(15));
        WaitForHeld(app, 12);
        signal.Stop();
        await Task.Delay(350);
        AssertBlackImage(app, report, "trimmed-black", journal);
        app.Combo("GapBehaviorCombo").Select(1);
        Wait(app, () => app.Text("CurrentTrackLabel").Contains("Gap: Freeze") &&
            Math.Abs(PlaybackSeconds(app) - 10) < 0.15, "Trimmed Freeze seeks to final frame");
        E2EAssert.WaitUntil(() => CaptureImage(app, report, "trimmed-freeze", journal) > 0.5, TimeSpan.FromSeconds(5));
        double frozenPosition = PlaybackSeconds(app);
        await Task.Delay(500);
        Assert.InRange(Math.Abs(PlaybackSeconds(app) - frozenPosition), 0, 0.05);
        Assert.True(CaptureImage(app, report, "trimmed-freeze-stable", journal) > 0.5);
        app.Combo("GapBehaviorCombo").Select(0);
        AssertBlackImage(app, report, "trimmed-black-restored", journal);
        app.Combo("SyncModeCombo").Select(0);
        Wait(app, () => !app.Text("CurrentTrackLabel").Contains("Gap:"), "Silent Single clears gap");
        app.Combo("SyncModeCombo").Select(1);
        Wait(app, () => app.Text("CurrentTrackLabel").Contains("Gap: Black"), "Silent Continue restores gap");
        AssertBlackImage(app, report, "trimmed-continue-restored", journal);
        // Rewinding after mpv's keep-open EOF pause must restore the intended playback state.
        app.Combo("SyncModeCombo").Select(0);
        if (app.Button("BtnPlay").Name == "▶") app.Button("BtnPlay").Invoke();
        double beyondEnd = Math.Ceiling(media.MediaDuration.TotalSeconds) + 2;
        signal.PlayHeld(beyondEnd, 25, TimeSpan.FromSeconds(15));
        WaitForHeld(app, beyondEnd);
        Wait(app, () => PlaybackSeconds(app) >= media.MediaDuration.TotalSeconds - 0.15, "Single reaches EOF");
        signal.PlayHeld(5, 25, TimeSpan.FromSeconds(15));
        WaitForHeld(app, 5);
        signal.Stop();
        await Task.Delay(350);
        Wait(app, () => PlaybackSeconds(app) > 5.3 && PlaybackSeconds(app) < 7, "Single rewinds and resumes after EOF");
        double afterRewind = PlaybackSeconds(app);
        Wait(app, () => PlaybackSeconds(app) > afterRewind + 0.15, "Single progresses without additional LTC seeks");
        journal.Write("single-eof-recovery-passed");
        Assert.True(app.ExitNormally(TimeSpan.FromSeconds(15)));
        Assert.Equal(0, app.Process.ExitCode);
        journal.Write("visible-freeze-passed", process: app.Process);
    }

    private static double End(TrackData track) => track.TimelineOffset.TotalSeconds + Math.Max(0, ((track.MediaOut ?? track.MediaDuration) - track.MediaIn).TotalSeconds);
    private static TrackData? ExpectedTrack(ProjectData project, double seconds) => project.Tracks.FirstOrDefault(t => t.IsEnabled && seconds >= t.TimelineOffset.TotalSeconds && seconds < End(t));
    private static bool Matches(E2EAppRunner app, TrackData? expected) => expected == null
        ? app.Text("CurrentTrackLabel").Contains("Gap: Black", StringComparison.Ordinal)
        : app.Text("CurrentTrackLabel") == $"Sync: {expected.Name}";
    private static void SetSync(E2EAppRunner app, bool enabled)
    {
        if (app.Button("BtnToggleSync").Name.Contains("ON", StringComparison.OrdinalIgnoreCase) != enabled) app.Button("BtnToggleSync").Invoke();
    }
    private static void WaitForHeld(E2EAppRunner app, double seconds) => Wait(app, () => Math.Abs(Seconds(app.Text("LtcTimecodeText"), 25) - seconds) < 0.015, $"Receive held LTC {seconds:F2}");
    private static double PlaybackSeconds(E2EAppRunner app)
    {
        Match rate = Regex.Match(app.Text("MetaLineText"), @"(\d+(?:\.\d+)?)\s*fps");
        return rate.Success ? Seconds(app.Text("TimeLabel").Split('/')[0].Trim(),
            double.Parse(rate.Groups[1].Value, CultureInfo.InvariantCulture)) : double.NaN;
    }
    private static double Seconds(string value, double fps)
    {
        string[] parts = value.Split(':');
        if (parts.Length != 4 || !parts.All(p => double.TryParse(p, NumberStyles.Number, CultureInfo.InvariantCulture, out _))) return -1;
        double[] n = parts.Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return n[0] * 3600 + n[1] * 60 + n[2] + n[3] / fps;
    }
    private static void Wait(E2EAppRunner app, Func<bool> condition, string description)
    {
        try { E2EAssert.WaitUntil(() => !app.Process.HasExited && condition(), TimeSpan.FromSeconds(6)); }
        catch (TimeoutException ex) { throw new TimeoutException(description + $"; track={app.Text("CurrentTrackLabel")}; time={app.Text("TimeLabel")}; ltc={app.Text("LtcTimecodeText")}", ex); }
    }
    private static void AssertBlackImage(E2EAppRunner app, string report, string name, MonkeyJournal journal)
    {
        // A label alone cannot prove that stale video was actually replaced with black.
        E2EAssert.WaitUntil(() => CaptureImage(app, report, name, journal) < 0.01, TimeSpan.FromSeconds(3));
    }
    private static double CaptureImage(E2EAppRunner app, string report, string name, MonkeyJournal journal)
    {
        var image = app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("VideoImage"));
        Assert.NotNull(image);
        app.MainWindow.Focus();
        using var capture = FlaUI.Core.Capturing.Capture.Element(image);
        capture.ToFile(Path.Combine(report, name + ".png"));
        int bright = 0, count = 0;
        for (int y = capture.Bitmap.Height / 10; y < capture.Bitmap.Height * 9 / 10; y += 8)
        for (int x = capture.Bitmap.Width / 10; x < capture.Bitmap.Width * 9 / 10; x += 8)
        {
            var pixel = capture.Bitmap.GetPixel(x, y);
            if (pixel.R > 12 || pixel.G > 12 || pixel.B > 12) bright++;
            count++;
        }
        Assert.True(count > 0);
        double fraction = bright / (double)count;
        journal.Write("image", details: new { name, nonBlackFraction = fraction });
        return fraction;
    }
}
