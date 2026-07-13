using System.IO;
using FluentAssertions;
using FlaUI.Core.AutomationElements;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests.E2E;

[Trait("Category", "E2E")]
[Collection("E2E")]
public sealed class WorkflowE2ETests
{
    [SkippableFact]
    public async Task LoadProject_RestoresContinueFreezeSelectionInUi()
    {
        var prereqs = E2EAppRunner.ResolvePrereqs();
        Skip.If(prereqs.SkipReason != null, prereqs.SkipReason);

        string projectPath = ProjectFileFactory.CreateTempProjectPath();
        try
        {
            string videoPath = TestVideoFactory.GetOrCreate();
            await SaveProjectAsync(projectPath, SyncMode.Continue, GapBehavior.Freeze, CreateTrack(videoPath, "clip-a", 0, 20));

            using var app = E2EAppRunner.Start(prereqs.ExePath, $"--vo null --load-project \"{projectPath}\"");

            ComboBox syncModeCombo = app.Combo("SyncModeCombo");
            ComboBox gapBehaviorCombo = app.Combo("GapBehaviorCombo");

            E2EAssert.WaitUntil(() => gapBehaviorCombo.IsEnabled, TimeSpan.FromSeconds(5));
            syncModeCombo.SelectedItem?.Name.Should().Contain("Continue");
            gapBehaviorCombo.IsEnabled.Should().BeTrue();
            gapBehaviorCombo.SelectedItem?.Name.Should().Contain("Freeze");
        }
        finally
        {
            ProjectFileFactory.Cleanup(projectPath);
        }
    }

    [SkippableFact]
    public async Task DistinctPlaylist_NextPreviousUpdatesTrackNames()
    {
        var prereqs = E2EAppRunner.ResolvePrereqs();
        Skip.If(prereqs.SkipReason != null, prereqs.SkipReason);

        string first = TestVideoFactory.GetOrCreate();
        string second = TestVideoFactory.GetOrCreateVariant("alt");

        using var app = E2EAppRunner.Start(prereqs.ExePath, $"--vo null --playlist \"{first}\" \"{second}\"");

        E2EAssert.WaitUntil(() => app.Text("CurrentTrackLabel").StartsWith("1/2", StringComparison.Ordinal), TimeSpan.FromSeconds(8));
        app.Text("CurrentTrackLabel").Should().Contain(Path.GetFileNameWithoutExtension(first));

        app.Button("BtnNextTrack").Invoke();
        E2EAssert.WaitUntil(() => app.Text("CurrentTrackLabel").StartsWith("2/2", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        app.Text("CurrentTrackLabel").Should().Contain(Path.GetFileNameWithoutExtension(second));

        app.Button("BtnPreviousTrack").Invoke();
        E2EAssert.WaitUntil(() => app.Text("CurrentTrackLabel").StartsWith("1/2", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        app.Text("CurrentTrackLabel").Should().Contain(Path.GetFileNameWithoutExtension(first));
    }

    [SkippableFact]
    public void PlaybackNearTrackEnd_AdvancesToNextTrack()
    {
        var prereqs = E2EAppRunner.ResolvePrereqs();
        Skip.If(prereqs.SkipReason != null, prereqs.SkipReason);

        string first = TestVideoFactory.GetOrCreateVariant("end_a");
        string second = TestVideoFactory.GetOrCreateVariant("end_b");

        using var app = E2EAppRunner.Start(prereqs.ExePath, $"--vo null --playlist \"{first}\" \"{second}\"");

        E2EAssert.WaitUntil(() => app.Text("CurrentTrackLabel").StartsWith("1/2", StringComparison.Ordinal), TimeSpan.FromSeconds(8));
        WaitForDuration(app);
        app.Slider("SeekBar").Patterns.RangeValue.Pattern.SetValue(0.98);
        E2EAssert.WaitUntil(() => ParseCurrentSeconds(app.Text("TimeLabel")) >= 18, TimeSpan.FromSeconds(5));
        app.Button("BtnPlay").Invoke();

        E2EAssert.WaitUntil(() => app.Text("CurrentTrackLabel").StartsWith("2/2", StringComparison.Ordinal), TimeSpan.FromSeconds(8));
        app.Text("CurrentTrackLabel").Should().Contain(Path.GetFileNameWithoutExtension(second));
    }

    [SkippableFact]
    public void ContinueFreeze_FinalTrackEnd_ShowsGapFreeze()
    {
        var prereqs = E2EAppRunner.ResolvePrereqs();
        Skip.If(prereqs.SkipReason != null, prereqs.SkipReason);

        string videoPath = TestVideoFactory.GetOrCreateVariant("freeze");

        using var app = E2EAppRunner.Start(prereqs.ExePath, $"--vo null --playlist \"{videoPath}\"");

        app.Combo("SyncModeCombo").Select(1);
        E2EAssert.WaitUntil(() => app.Combo("GapBehaviorCombo").IsEnabled, TimeSpan.FromSeconds(5));
        app.Combo("GapBehaviorCombo").Select(1);
        WaitForDuration(app);
        app.Slider("SeekBar").Patterns.RangeValue.Pattern.SetValue(0.98);
        E2EAssert.WaitUntil(() => ParseCurrentSeconds(app.Text("TimeLabel")) >= 18, TimeSpan.FromSeconds(5));
        app.Button("BtnPlay").Invoke();

        E2EAssert.WaitUntil(() => app.Text("CurrentTrackLabel").Contains("Gap: Freeze", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        app.Button("BtnPlay").Name.Should().Be("▶");
    }

    private static async Task SaveProjectAsync(string path, SyncMode syncMode, GapBehavior gapBehavior, params PlaylistTrack[] tracks)
    {
        var playlist = new PlaylistState();
        foreach (PlaylistTrack track in tracks)
            playlist.Tracks.Add(track);
        if (tracks.Length > 0)
            playlist.Select(0);

        await ProjectSerializer.SaveAsync(path, playlist, syncMode, gapBehavior);
    }

    private static PlaylistTrack CreateTrack(string path, string name, double timelineOffsetSeconds, double durationSeconds)
        => new(
            Id: Guid.NewGuid(),
            FilePath: path,
            Name: name,
            MediaIn: TimeSpan.Zero,
            MediaOut: null,
            TimelineOffset: TimeSpan.FromSeconds(timelineOffsetSeconds),
            MediaDuration: TimeSpan.FromSeconds(durationSeconds),
            SyncOffset: TimeSpan.Zero,
            FrameRate: 30.0,
            IsEnabled: true);

    private static void WaitForDuration(E2EAppRunner app)
    {
        E2EAssert.WaitUntil(() => app.Text("TimeLabel").Contains("/"), TimeSpan.FromSeconds(8));
    }

    private static double ParseCurrentSeconds(string timeLabelText)
    {
        string current = timeLabelText.Split('/')[0].Trim();
        string[] parts = current.Split(':');
        if (parts.Length < 3)
            return 0;

        return int.Parse(parts[0]) * 3600
            + int.Parse(parts[1]) * 60
            + int.Parse(parts[2]);
    }
}
