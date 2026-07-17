using System.IO;
using System.Text.Json;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.E2E;

[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class SettingsPersistenceE2ETests
{
    [SkippableFact]
    public void SyncGapAndLtcDevice_ChangeExitAndRestart_RestoresSelections()
    {
        var prereqs = E2EAppRunner.ResolvePrereqs();
        Skip.If(prereqs.SkipReason != null, prereqs.SkipReason);
        string directory = CreateTemporaryDirectory();
        string settingsPath = Path.Combine(directory, "settings.json");

        try
        {
            string selectedDevice;
            using (var app = E2EAppRunner.Start(prereqs.ExePath, "--vo null", settingsPath))
            {
                var deviceCombo = app.Combo("LtcDeviceCombo");
                deviceCombo.Items.Length.Should().BeGreaterThan(1,
                    "the test environment must expose a user-selectable capture device alternative");
                int deviceIndex = deviceCombo.Items.Length - 1;
                selectedDevice = deviceCombo.Items[deviceIndex].Name;

                app.Combo("SyncModeCombo").Select(1);
                E2EAssert.WaitUntil(() => app.Combo("GapBehaviorCombo").IsEnabled, TimeSpan.FromSeconds(3));
                app.Combo("GapBehaviorCombo").Select(0);
                deviceCombo.Select(deviceIndex);

                E2EAssert.WaitUntil(
                    () => ReadSettings(settingsPath) is { } settings &&
                          settings.SyncMode == SyncMode.Continue &&
                          settings.GapBehavior == GapBehavior.Black &&
                          settings.LtcDeviceName == selectedDevice,
                    TimeSpan.FromSeconds(5));

                app.Process.CloseMainWindow().Should().BeTrue();
                app.Process.WaitForExit(5000).Should().BeTrue("the application should exit normally");
            }

            using var restarted = E2EAppRunner.Start(prereqs.ExePath, "--vo null", settingsPath);
            restarted.Combo("SyncModeCombo").SelectedItem?.Name.Should().Contain("Continue");
            restarted.Combo("GapBehaviorCombo").SelectedItem?.Name.Should().Contain("Black");
            restarted.Combo("LtcDeviceCombo").SelectedItem?.Name.Should().Be(selectedDevice);
        }
        finally
        {
            E2ESettingsIsolation.Delete(directory);
        }
    }

    [SkippableFact]
    public async Task SuccessfulProjectLoad_RecordsPathWithoutAutoOpeningOnRestart()
    {
        var prereqs = E2EAppRunner.ResolvePrereqs();
        Skip.If(prereqs.SkipReason != null, prereqs.SkipReason);
        string directory = CreateTemporaryDirectory();
        string settingsPath = Path.Combine(directory, "settings.json");
        string projectPath = Path.Combine(directory, "remembered.tsp");
        string videoPath = TestVideoFactory.GetOrCreate();
        var playlist = new PlaylistState();
        playlist.AddFiles([videoPath]);
        await ProjectSerializer.SaveAsync(projectPath, playlist, SyncMode.Continue, GapBehavior.Freeze);

        try
        {
            using (var app = E2EAppRunner.Start(
                       prereqs.ExePath,
                       $"--vo null --load-project \"{projectPath}\"",
                       settingsPath,
                       pausePlaybackIfNeeded: false))
            {
                E2EAssert.WaitUntil(
                    () => ReadSettings(settingsPath)?.LastOpenedProjectPath == projectPath,
                    TimeSpan.FromSeconds(5));
                app.Process.CloseMainWindow().Should().BeTrue();
                app.Process.WaitForExit(5000).Should().BeTrue();
            }

            using var restarted = E2EAppRunner.Start(prereqs.ExePath, "--vo null", settingsPath);
            restarted.Text("CurrentTrackLabel").Should().NotContain(Path.GetFileNameWithoutExtension(videoPath));
        }
        finally
        {
            E2ESettingsIsolation.Delete(directory);
        }
    }

    private static AppSettings? ReadSettings(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "TimecodeSyncPlayer.Tests",
            "settings-persistence",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
