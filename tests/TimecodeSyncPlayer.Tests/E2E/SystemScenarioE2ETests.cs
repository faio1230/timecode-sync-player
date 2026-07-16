using System.IO;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.E2E;

[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class SystemScenarioE2ETests
{
    [Fact]
    public void ProjectRoundTrip_RestoresPlaylistOrderOffsetModeAndPlayback()
    {
        (string exe, string video) = RequirePrerequisites();
        string videoCopy = CreateDialogFileCopy(video, "roundtrip_a");
        string alternateCopy = CreateDialogFileCopy(
            TestVideoFactory.GetOrCreateVariant("system_roundtrip"), "roundtrip_b");
        string projectPath = CreateDialogProjectFile("roundtrip");

        try
        {
            using var app = E2EAppRunner.Start(exe, "--vo null");
            AddPlaylistFiles(app, videoCopy, alternateCopy);
            ListBox playlist = Playlist(app);
            E2EAssert.WaitUntil(() => playlist.Items.Length == 2, TimeSpan.FromSeconds(8));
            EnsurePaused(app);

            playlist.Items[1].Select();
            app.Button("BtnMoveTrackUp").Invoke();
            E2EAssert.WaitUntil(
                () => playlist.Items[0].Name.Contains(Path.GetFileNameWithoutExtension(alternateCopy), StringComparison.Ordinal),
                TimeSpan.FromSeconds(5));

            TextBox offset = playlist.Items[0]
                .FindFirstDescendant(cf => cf.ByAutomationId("TimelineOffsetTextBox"))!
                .AsTextBox();
            offset.Focus();
            offset.Text = "00:00:10:00";
            app.Button("BtnSaveProject").Focus();
            E2EAssert.WaitUntil(() => offset.Text.StartsWith("00:00:10", StringComparison.Ordinal), TimeSpan.FromSeconds(3));

            app.Combo("SyncModeCombo").Select(1);
            E2EAssert.WaitUntil(() => app.Combo("GapBehaviorCombo").IsEnabled, TimeSpan.FromSeconds(3));
            app.Combo("GapBehaviorCombo").Select(1);

            InvokeFileDialog(app, "BtnSaveProject", projectPath, confirmOverwrite: true);
            E2EAssert.WaitUntil(() => File.Exists(projectPath) && new FileInfo(projectPath).Length > 0, TimeSpan.FromSeconds(8));

            app.Button("BtnClearPlaylist").Invoke();
            E2EAssert.WaitUntil(() => playlist.Items.Length == 0, TimeSpan.FromSeconds(5));
            InvokeFileDialog(app, "BtnLoadProject", projectPath);
            E2EAssert.WaitUntil(() => playlist.Items.Length == 2, TimeSpan.FromSeconds(8));

            playlist.Items[0].Name.Should().Contain(Path.GetFileNameWithoutExtension(alternateCopy));
            playlist.Items[1].Name.Should().Contain(Path.GetFileNameWithoutExtension(videoCopy));
            playlist.Items[0]
                .FindFirstDescendant(cf => cf.ByAutomationId("TimelineOffsetTextBox"))!
                .AsTextBox().Text.Should().StartWith("00:00:10");
            app.Combo("SyncModeCombo").SelectedItem?.Name.Should().Contain("Continue");
            app.Combo("GapBehaviorCombo").SelectedItem?.Name.Should().Contain("Freeze");

            WaitForDuration(app);
            app.Slider("SeekBar").Patterns.RangeValue.Pattern.SetValue(0.25);
            E2EAssert.WaitUntil(() => CurrentSeconds(app) > 0, TimeSpan.FromSeconds(5));
            double beforePlay = CurrentSeconds(app);
            EnsurePlaying(app);
            E2EAssert.WaitUntil(() => CurrentSeconds(app) > beforePlay, TimeSpan.FromSeconds(5));
        }
        finally
        {
            DeleteDialogFile(projectPath);
            DeleteDialogFile(videoCopy);
            DeleteDialogFile(alternateCopy);
        }
    }

    [Fact]
    public void ModeSwitchWorkflow_PreservesPlaybackAndTimelineState()
    {
        (string exe, string video) = RequirePrerequisites();
        using var app = E2EAppRunner.Start(exe, $"--vo null --playlist \"{video}\"");
        WaitForDuration(app);
        SeekToEarlyPosition(app);
        app.Button("BtnTimeline").Invoke();
        string timelineLabel = app.Button("BtnTimeline").Name;
        EnsurePlaying(app);
        double start = CurrentSeconds(app);

        app.Combo("SyncModeCombo").Select(1);
        E2EAssert.WaitUntil(() => app.Combo("GapBehaviorCombo").IsEnabled, TimeSpan.FromSeconds(3));
        E2EAssert.WaitUntil(() => CurrentSeconds(app) > start, TimeSpan.FromSeconds(5));
        double afterContinue = CurrentSeconds(app);

        app.Combo("SyncModeCombo").Select(0);
        E2EAssert.WaitUntil(() => !app.Combo("GapBehaviorCombo").IsEnabled, TimeSpan.FromSeconds(3));
        E2EAssert.WaitUntil(() => CurrentSeconds(app) >= afterContinue, TimeSpan.FromSeconds(3));

        app.Combo("SyncModeCombo").SelectedItem?.Name.Should().Contain("Single");
        double finalPosition = CurrentSeconds(app);
        double finalDuration = DurationSeconds(app);
        // TimeLabel is rendered at whole-second precision. Treat its final displayed
        // second as the natural end so the assertion does not depend on sub-second mpv state.
        bool reachedEnd = finalPosition >= finalDuration - 1.0;
        app.Button("BtnPlay").Name.Should().Be(reachedEnd ? "▶" : "⏸",
            $"the play button must match playback state; start={start}, afterContinue={afterContinue}, final={finalPosition}, duration={finalDuration}");
        app.Button("BtnTimeline").Name.Should().Be(timelineLabel);
    }

    [Fact]
    public void FullscreenDuringPlayback_TwoCyclesPreservePlaybackAndSpoutState()
    {
        (string exe, string video) = RequirePrerequisites();
        using var app = E2EAppRunner.Start(exe, $"--vo null --playlist \"{video}\"");
        WaitForDuration(app);
        SeekToEarlyPosition(app);
        E2EAssert.WaitUntil(() => app.Button("BtnFullscreen").IsEnabled, TimeSpan.FromSeconds(5));
        string spoutLabel = app.Button("BtnSpout").Name;
        EnsurePlaying(app);
        double start = CurrentSeconds(app);

        for (int cycle = 0; cycle < 2; cycle++)
        {
            app.Button("BtnFullscreen").Invoke();
            E2EAssert.WaitUntil(
                () => app.Button("BtnFullscreen").Name == "EXIT FULLSCREEN",
                TimeSpan.FromSeconds(5));
            E2EAssert.WaitUntil(() => FullscreenWindow(app) != null, TimeSpan.FromSeconds(5));
            app.Button("BtnFullscreen").Name.Should().Be("EXIT FULLSCREEN");

            app.Button("BtnFullscreen").Invoke();
            E2EAssert.WaitUntil(() => FullscreenWindow(app) == null, TimeSpan.FromSeconds(5));
            app.Button("BtnFullscreen").Name.Should().Be("FULLSCREEN");
        }

        E2EAssert.WaitUntil(() => CurrentSeconds(app) > start, TimeSpan.FromSeconds(5));
        app.Button("BtnPlay").Name.Should().Be("⏸");
        app.Button("BtnSpout").Name.Should().Be(spoutLabel);
    }

    [Fact]
    public void CorruptProjectLoad_ShowsErrorAndApplicationRemainsUsable()
    {
        (string exe, string video) = RequirePrerequisites();
        string projectPath = CreateDialogProjectFile("corrupt");
        File.WriteAllText(projectPath, "{\"tracks\":[");

        try
        {
            using var app = E2EAppRunner.Start(exe, $"--vo null --playlist \"{video}\"");
            InvokeFileDialog(app, "BtnLoadProject", projectPath, expectFollowupDialog: true);

            Window error = WaitForModalWindow(app, window => window.Name.Contains("エラー", StringComparison.Ordinal));
            error.Name.Should().Contain("エラー");
            Button ok = error.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Select(element => element.AsButton())
                .First();
            ok.Invoke();
            E2EAssert.WaitUntil(() => app.MainWindow.ModalWindows.Length == 0, TimeSpan.FromSeconds(5));

            app.Process.HasExited.Should().BeFalse();
            EnsurePlaying(app);
            EnsurePaused(app);
        }
        finally
        {
            DeleteDialogFile(projectPath);
        }
    }

    private static (string ExePath, string VideoPath) RequirePrerequisites()
    {
        (string exePath, string? reason) = E2EAppRunner.ResolvePrereqs();
        reason.Should().BeNull("V9 is a mandatory no-Skip E2E gate");
        return (exePath, TestVideoFactory.GetOrCreate());
    }

    private static ListBox Playlist(E2EAppRunner app) =>
        app.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("PlaylistList"))!.AsListBox();

    private static void AddPlaylistFiles(E2EAppRunner app, params string[] paths) =>
        InvokeFileDialog(app, "BtnAddToPlaylist", paths);

    private static void InvokeFileDialog(
        E2EAppRunner app,
        string buttonAutomationId,
        string path,
        bool confirmOverwrite = false,
        bool expectFollowupDialog = false) =>
        InvokeFileDialog(app, buttonAutomationId, [path], confirmOverwrite, expectFollowupDialog);

    private static void InvokeFileDialog(
        E2EAppRunner app,
        string buttonAutomationId,
        IReadOnlyList<string> paths,
        bool confirmOverwrite = false,
        bool expectFollowupDialog = false)
    {
        Button button = app.Button(buttonAutomationId);
        Task invokeTask = Task.Run(button.Invoke);
        Window dialog = WaitForModalWindow(app, _ => true);
        NavigateToVideosFolder(dialog);
        for (int index = 0; index < paths.Count; index++)
        {
            string fileName = Path.GetFileName(paths[index]);
            AutomationElement? fileItem = null;
            E2EAssert.WaitUntil(() =>
            {
                fileItem = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .FirstOrDefault(element => element.Name == fileName);
                return fileItem != null;
            }, TimeSpan.FromSeconds(5));
            if (index == 0)
                fileItem!.Patterns.SelectionItem.Pattern.Select();
            else
                fileItem!.Patterns.SelectionItem.Pattern.AddToSelection();
        }
        AutomationElement accept = dialog.FindAllDescendants()
            .Single(element =>
                element.Properties.AutomationId.ValueOrDefault == "1" &&
                element.ControlType is ControlType.Button or ControlType.SplitButton);
        nint dialogHandle = dialog.Properties.NativeWindowHandle.ValueOrDefault;
        accept.Patterns.Invoke.Pattern.Invoke();

        if (confirmOverwrite)
        {
            Window? confirmation = null;
            E2EAssert.WaitUntil(() =>
            {
                Window[] modals = app.MainWindow.ModalWindows;
                confirmation = modals.FirstOrDefault(window =>
                    window.Properties.NativeWindowHandle.ValueOrDefault != dialogHandle);
                return confirmation != null || modals.All(window =>
                    window.Properties.NativeWindowHandle.ValueOrDefault != dialogHandle);
            }, TimeSpan.FromSeconds(8));
            confirmation.Should().NotBeNull("an existing project file requires overwrite confirmation");
            Button yes = confirmation!.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .Select(element => element.AsButton())
                .First(button => button.Name.StartsWith("はい", StringComparison.Ordinal));
            yes.Invoke();
        }

        E2EAssert.WaitUntil(
            () => app.MainWindow.ModalWindows.All(window =>
                window.Properties.NativeWindowHandle.ValueOrDefault != dialogHandle),
            TimeSpan.FromSeconds(8));

        if (!expectFollowupDialog)
        {
            E2EAssert.WaitUntil(() => app.MainWindow.ModalWindows.Length == 0, TimeSpan.FromSeconds(8));
            E2EAssert.WaitUntil(() => invokeTask.IsCompleted, TimeSpan.FromSeconds(8));
            invokeTask.GetAwaiter().GetResult();
        }
    }

    private static void NavigateToVideosFolder(Window dialog)
    {
        AutomationElement? videos = null;
        try
        {
            E2EAssert.WaitUntil(() =>
            {
                videos = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem))
                    .Concat(dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)))
                    .FirstOrDefault(element =>
                        element.Name.StartsWith("Videos", StringComparison.Ordinal) ||
                        element.Name.StartsWith("ビデオ", StringComparison.Ordinal));
                return videos != null;
            }, TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException ex)
        {
            string available = string.Join(" | ", dialog.FindAllDescendants()
                .Select(element => $"{element.ControlType}:{element.Name}")
                .Where(value => !value.EndsWith(':'))
                .Distinct());
            throw new TimeoutException($"Videos navigation item was unavailable. Elements: {available}", ex);
        }
        videos!.Patterns.SelectionItem.Pattern.Select();
    }

    private static Window WaitForModalWindow(E2EAppRunner app, Func<Window, bool> predicate)
    {
        Window? result = null;
        E2EAssert.WaitUntil(() =>
        {
            result = app.MainWindow.ModalWindows.FirstOrDefault(predicate);
            return result != null;
        }, TimeSpan.FromSeconds(8));
        return result!;
    }

    private static Window? FullscreenWindow(E2EAppRunner app) =>
        app.MainWindow.Automation.GetDesktop()
            .FindFirstDescendant(cf => cf.ByAutomationId("FullscreenOutputWindow"))
            ?.AsWindow();

    private static string CreateDialogFileCopy(string sourcePath, string suffix)
    {
        string directory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            $"000-tsp-v9-{suffix}-{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");
        File.Copy(sourcePath, path);
        return path;
    }

    private static string CreateDialogProjectFile(string suffix)
    {
        string directory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"000-tsp-v9-{suffix}-{Guid.NewGuid():N}.tsp");
        File.WriteAllText(path, "{}");
        return path;
    }

    private static void DeleteDialogFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void WaitForDuration(E2EAppRunner app) =>
        E2EAssert.WaitUntil(() => app.Text("TimeLabel").Contains('/'), TimeSpan.FromSeconds(8));

    private static void SeekToEarlyPosition(E2EAppRunner app)
    {
        app.Slider("SeekBar").Patterns.RangeValue.Pattern.SetValue(0.1);
        E2EAssert.WaitUntil(
            () => CurrentSeconds(app) is > 0 and < 5,
            TimeSpan.FromSeconds(5));
    }

    private static void EnsurePlaying(E2EAppRunner app)
    {
        Button play = app.Button("BtnPlay");
        if (play.Name == "▶")
            play.Invoke();
        E2EAssert.WaitUntil(() => play.Name == "⏸", TimeSpan.FromSeconds(3));
    }

    private static void EnsurePaused(E2EAppRunner app)
    {
        Button play = app.Button("BtnPlay");
        if (play.Name == "⏸")
            play.Invoke();
        E2EAssert.WaitUntil(() => play.Name == "▶", TimeSpan.FromSeconds(3));
    }

    private static double CurrentSeconds(E2EAppRunner app)
    {
        string current = app.Text("TimeLabel").Split('/')[0].Trim();
        string[] parts = current.Split(':');
        return parts.Length < 3
            ? 0
            : int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + double.Parse(parts[2]);
    }

    private static double DurationSeconds(E2EAppRunner app)
    {
        string[] sides = app.Text("TimeLabel").Split('/');
        if (sides.Length < 2)
            return 0;
        string[] parts = sides[1].Trim().Split(':');
        return parts.Length < 3
            ? 0
            : int.Parse(parts[0]) * 3600 + int.Parse(parts[1]) * 60 + double.Parse(parts[2]);
    }
}
