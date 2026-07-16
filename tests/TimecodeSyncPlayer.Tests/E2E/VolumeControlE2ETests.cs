using System.IO;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class VolumeControlE2ETests
{
    private const int Fps = 25;

    [Fact]
    public void MuteAndVolume_PersistAndRestoreAcrossApplicationRestart()
    {
        (string exe, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(skipReason != null, skipReason);
        string video = TestVideoFactory.GetOrCreate();
        string directory = Path.Combine(Path.GetTempPath(), "TimecodeSyncPlayer.Tests", "volume", Guid.NewGuid().ToString("N"));
        string settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            using (var app = E2EAppRunner.Start(exe, $"--vo null --open \"{video}\"", settingsPath))
            {
                app.Button("BtnMute").Name.Should().Be("MUTE OFF");
                app.Slider("VolumeSlider").Value.Should().Be(100);

                app.Button("BtnMute").Invoke();
                app.Slider("VolumeSlider").Patterns.RangeValue.Pattern.SetValue(37);

                E2EAssert.WaitUntil(
                    () => ReadAudioSettings(settingsPath) == (true, 37),
                    TimeSpan.FromSeconds(5));
                app.Button("BtnMute").Name.Should().Be("MUTE ON");
                app.Slider("VolumeSlider").Value.Should().Be(37);
            }

            using var restarted = E2EAppRunner.Start(exe, $"--vo null --open \"{video}\"", settingsPath);
            E2EAssert.WaitUntil(
                () => restarted.Button("BtnMute").Name == "MUTE ON" &&
                      Math.Abs(restarted.Slider("VolumeSlider").Value - 37) < 0.01,
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            E2ESettingsIsolation.Delete(directory);
        }
    }

    [SkippableFact]
    public async Task CableLoop_ContinueMuteSurvivesDeterministicGapAndTrackSwitch()
    {
        (string exe, string? skipReason) = E2EAppRunner.ResolvePrereqs();
        Skip.If(skipReason != null, skipReason);
        string? captureDeviceName = LtcSignalPlayer.FindCableCaptureDeviceName();
        Skip.If(captureDeviceName == null,
            "有効な VB-CABLE 録音デバイス（CABLE Output）が見つかりません。");
        bool cableAvailable = LtcSignalPlayer.TryCreateCablePlayer(
            out LtcSignalPlayer? signalPlayer,
            out string? cableSkipReason);
        Skip.If(!cableAvailable, cableSkipReason ?? "CABLE Input を利用できません。");

        string directory = Path.Combine(
            Path.GetTempPath(),
            "TimecodeSyncPlayer.Tests",
            "volume-hardware",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string settingsPath = Path.Combine(directory, "settings.json");
        string projectPath = Path.Combine(directory, "volume-gap.tsp.json");
        string sourceVideo = TestVideoFactory.GetOrCreate();
        string projectVideo = Path.Combine(directory, Path.GetFileName(sourceVideo));
        File.Copy(sourceVideo, projectVideo);
        await SaveGapProjectAsync(projectPath, projectVideo);

        using (signalPlayer)
        {
            try
            {
                using var app = E2EAppRunner.Start(
                    exe,
                    $"--vo null --load-project \"{projectPath}\"",
                    settingsPath);
                SelectCableCaptureDevice(app);
                app.Combo("LtcFpsModeCombo").Select(2);
                E2EAssert.WaitUntil(
                    () => app.Combo("LtcFpsModeCombo").SelectedItem?.Name.Contains("25", StringComparison.Ordinal) == true,
                    TimeSpan.FromSeconds(3));

                app.Button("BtnMute").Invoke();
                E2EAssert.WaitUntil(
                    () => app.Button("BtnMute").Name == "MUTE ON" && ReadAudioSettings(settingsPath).IsMuted,
                    TimeSpan.FromSeconds(5));

                string logPath = GetLatestApplicationLogPath();
                long logOffset = new FileInfo(logPath).Length;
                signalPlayer!.Play(
                    new LtcTimecode(0, 0, 3, 12, false),
                    Fps,
                    TimeSpan.FromSeconds(12));
                app.Button("BtnStartLtc").Invoke();
                if (!app.Button("BtnToggleSync").Name.Contains("ON", StringComparison.OrdinalIgnoreCase))
                    app.Button("BtnToggleSync").Invoke();

                E2EAssert.WaitUntil(
                    () => ReadLogSince(logPath, logOffset).Contains(
                        "switching to track volume-track-1",
                        StringComparison.Ordinal),
                    TimeSpan.FromSeconds(3));
                AssertMutePreserved(app, settingsPath);

                E2EAssert.WaitUntil(
                    () => app.Text("CurrentTrackLabel").Contains("Gap: Black", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5));
                AssertMutePreserved(app, settingsPath);

                E2EAssert.WaitUntil(
                    () => ReadLogSince(logPath, logOffset).Contains(
                        "switching to track volume-track-2",
                        StringComparison.Ordinal),
                    TimeSpan.FromSeconds(6));
                AssertMutePreserved(app, settingsPath);
            }
            finally
            {
                signalPlayer!.Stop();
                E2ESettingsIsolation.Delete(directory);
            }
        }
    }

    private static async Task SaveGapProjectAsync(string path, string video)
    {
        var playlist = new PlaylistState();
        playlist.Tracks.Add(CreateTrack(video, "volume-track-1", timelineOffsetSeconds: 0));
        playlist.Tracks.Add(CreateTrack(video, "volume-track-2", timelineOffsetSeconds: 8));
        playlist.Select(0);
        await ProjectSerializer.SaveAsync(path, playlist, SyncMode.Continue, GapBehavior.Black);
    }

    private static PlaylistTrack CreateTrack(string path, string name, double timelineOffsetSeconds) => new(
        Id: Guid.NewGuid(),
        FilePath: path,
        Name: name,
        MediaIn: TimeSpan.Zero,
        MediaOut: TimeSpan.FromSeconds(5),
        TimelineOffset: TimeSpan.FromSeconds(timelineOffsetSeconds),
        MediaDuration: TimeSpan.FromSeconds(20),
        SyncOffset: TimeSpan.Zero,
        FrameRate: Fps,
        IsEnabled: true);

    private static void SelectCableCaptureDevice(E2EAppRunner app)
    {
        app.Button("BtnRefreshLtcDevices").Invoke();
        ComboBox deviceCombo = app.Combo("LtcDeviceCombo");
        int selectedIndex = -1;
        E2EAssert.WaitUntil(
            () =>
            {
                selectedIndex = Array.FindIndex(
                    deviceCombo.Items,
                    item => item.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase));
                return selectedIndex >= 0;
            },
            TimeSpan.FromSeconds(5));
        deviceCombo.Select(selectedIndex);
        E2EAssert.WaitUntil(
            () => deviceCombo.SelectedItem?.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase) == true,
            TimeSpan.FromSeconds(3));
    }

    private static void AssertMutePreserved(E2EAppRunner app, string settingsPath)
    {
        app.Button("BtnMute").Name.Should().Be("MUTE ON");
        ReadAudioSettings(settingsPath).IsMuted.Should().BeTrue();
    }

    private static string GetLatestApplicationLogPath()
    {
        string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        return Directory.EnumerateFiles(logDirectory, "timecodesyncplayer-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    private static string ReadLogSince(string path, long offset)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Position = Math.Min(offset, stream.Length);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static (bool IsMuted, double Volume) ReadAudioSettings(string path)
    {
        if (!File.Exists(path))
            return default;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            return (root.GetProperty("isMuted").GetBoolean(), root.GetProperty("volume").GetDouble());
        }
        catch (IOException)
        {
            return default;
        }
        catch (JsonException)
        {
            return default;
        }
    }
}
