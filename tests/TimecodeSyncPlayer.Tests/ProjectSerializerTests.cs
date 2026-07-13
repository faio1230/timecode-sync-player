using System.IO;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ProjectSerializerTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectSerializerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TimecodeSyncPlayer.Tests", "ProjectSerializer");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private string GetTempPath(string fileName) => Path.Combine(_tempDir, fileName);

    [Fact]
    public async Task SaveAndLoad_RoundTrip_PreservesAllFields()
    {
        var tempFile1 = GetTempPath("clip1.mp4");
        var tempFile2 = GetTempPath("clip2.mp4");
        await File.WriteAllTextAsync(tempFile1, "");
        await File.WriteAllTextAsync(tempFile2, "");

        try
        {
            var playlist = new PlaylistState();
            playlist.AddFiles([tempFile1, tempFile2]);

            var track0 = playlist.Tracks[0];
            var updatedTrack0 = track0 with
            {
                MediaIn = TimeSpan.FromSeconds(5),
                MediaOut = TimeSpan.FromSeconds(30),
                TimelineOffset = TimeSpan.FromSeconds(2),
                MediaDuration = TimeSpan.FromSeconds(60),
                SyncOffset = TimeSpan.FromMilliseconds(-100),
                FrameRate = 29.97,
                IsEnabled = true,
                Name = "Custom Name 1"
            };
            playlist.Tracks[0] = updatedTrack0;

            var track1 = playlist.Tracks[1];
            var updatedTrack1 = track1 with
            {
                MediaIn = TimeSpan.Zero,
                MediaOut = null,
                TimelineOffset = TimeSpan.Zero,
                MediaDuration = TimeSpan.FromSeconds(45),
                SyncOffset = TimeSpan.Zero,
                FrameRate = 24,
                IsEnabled = false,
                Name = "Custom Name 2"
            };
            playlist.Tracks[1] = updatedTrack1;

            string filePath = GetTempPath("roundtrip.tsp");

            await ProjectSerializer.SaveAsync(filePath, playlist, SyncMode.Continue, GapBehavior.Black);

            var project = await ProjectSerializer.LoadAsync(filePath);

            project.Should().NotBeNull();
            project!.Version.Should().Be(1);
            project.SyncMode.Should().Be(SyncMode.Continue);
            project.GapBehavior.Should().Be(GapBehavior.Black);
            project.Tracks.Should().HaveCount(2);

            var loaded0 = project.Tracks[0];
            loaded0.Id.Should().Be(updatedTrack0.Id);
            loaded0.FilePath.Should().Be(Path.GetFullPath(tempFile1));
            loaded0.Name.Should().Be("Custom Name 1");
            loaded0.MediaIn.Should().Be(TimeSpan.FromSeconds(5));
            loaded0.MediaOut.Should().Be(TimeSpan.FromSeconds(30));
            loaded0.TimelineOffset.Should().Be(TimeSpan.FromSeconds(2));
            loaded0.MediaDuration.Should().Be(TimeSpan.FromSeconds(60));
            loaded0.SyncOffset.Should().Be(TimeSpan.FromMilliseconds(-100));
            loaded0.FrameRate.Should().Be(29.97);
            loaded0.IsEnabled.Should().BeTrue();

            var loaded1 = project.Tracks[1];
            loaded1.Id.Should().Be(updatedTrack1.Id);
            loaded1.FilePath.Should().Be(Path.GetFullPath(tempFile2));
            loaded1.Name.Should().Be("Custom Name 2");
            loaded1.MediaIn.Should().Be(TimeSpan.Zero);
            loaded1.MediaOut.Should().BeNull();
            loaded1.TimelineOffset.Should().Be(TimeSpan.Zero);
            loaded1.MediaDuration.Should().Be(TimeSpan.FromSeconds(45));
            loaded1.SyncOffset.Should().Be(TimeSpan.Zero);
            loaded1.FrameRate.Should().Be(24);
            loaded1.IsEnabled.Should().BeFalse();
        }
        finally
        {
            File.Delete(tempFile1);
            File.Delete(tempFile2);
        }
    }

    [Fact]
    public async Task SaveLoadApply_RoundTrip_RestoresTracks()
    {
        var tempFile1 = GetTempPath("apply_roundtrip_1.mp4");
        var tempFile2 = GetTempPath("apply_roundtrip_2.mp4");
        await File.WriteAllTextAsync(tempFile1, "");
        await File.WriteAllTextAsync(tempFile2, "");

        try
        {
            var source = new PlaylistState();
            source.AddFiles([tempFile1, tempFile2]);
            source.Tracks[0] = source.Tracks[0] with
            {
                MediaDuration = TimeSpan.FromSeconds(20),
                FrameRate = 30,
                TimelineOffset = TimeSpan.FromSeconds(1)
            };
            source.Tracks[1] = source.Tracks[1] with
            {
                MediaDuration = TimeSpan.FromSeconds(20),
                FrameRate = 30,
                TimelineOffset = TimeSpan.FromSeconds(21)
            };

            string projectPath = GetTempPath("apply_roundtrip.tsp");
            await ProjectSerializer.SaveAsync(projectPath, source, SyncMode.Continue, GapBehavior.Freeze);

            var loaded = await ProjectSerializer.LoadAsync(projectPath);
            var restored = new PlaylistState();
            ProjectSerializer.ApplyToPlaylist(loaded!, restored);

            restored.Tracks.Should().HaveCount(2);
            restored.Tracks[0].FilePath.Should().Be(Path.GetFullPath(tempFile1));
            restored.Tracks[1].FilePath.Should().Be(Path.GetFullPath(tempFile2));
            restored.CurrentIndex.Should().Be(0);
        }
        finally
        {
            File.Delete(tempFile1);
            File.Delete(tempFile2);
        }
    }

    [Fact]
    public async Task LoadAsync_NonExistentFile_ThrowsFileNotFoundException()
    {
        string filePath = GetTempPath("does_not_exist.tsp");

        await FluentActions.Awaiting(() => ProjectSerializer.LoadAsync(filePath))
            .Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_ThrowsJsonException()
    {
        string filePath = GetTempPath("malformed.tsp");
        await File.WriteAllTextAsync(filePath, "{ this is not valid json !!! }", System.Text.Encoding.UTF8);

        await FluentActions.Awaiting(() => ProjectSerializer.LoadAsync(filePath))
            .Should().ThrowAsync<System.Text.Json.JsonException>();
    }

    [Fact]
    public async Task LoadAsync_EmptyJson_ThrowsOrReturnsNull()
    {
        string filePath = GetTempPath("empty.tsp");
        await File.WriteAllTextAsync(filePath, "", System.Text.Encoding.UTF8);

        await FluentActions.Awaiting(() => ProjectSerializer.LoadAsync(filePath))
            .Should().ThrowAsync<System.Text.Json.JsonException>();
    }

    [Fact]
    public async Task ApplyToPlaylist_RestoresTracksCorrectly()
    {
        var tempFile1 = GetTempPath("alpha.mp4");
        var tempFile2 = GetTempPath("beta.mp4");
        await File.WriteAllTextAsync(tempFile1, "");
        await File.WriteAllTextAsync(tempFile2, "");

        try
        {
            var project = new ProjectData
            {
                Version = 1,
                SyncMode = SyncMode.Continue,
                GapBehavior = GapBehavior.Freeze,
                Tracks =
                [
                    new TrackData
                    {
                        Id = Guid.NewGuid(),
                        FilePath = tempFile1,
                        Name = "Alpha Clip",
                        MediaIn = TimeSpan.FromSeconds(10),
                        MediaOut = TimeSpan.FromSeconds(50),
                        TimelineOffset = TimeSpan.FromSeconds(5),
                        MediaDuration = TimeSpan.FromSeconds(100),
                        SyncOffset = TimeSpan.FromMilliseconds(50),
                        FrameRate = 30,
                        IsEnabled = true
                    },
                    new TrackData
                    {
                        Id = Guid.NewGuid(),
                        FilePath = tempFile2,
                        Name = "Beta Clip",
                        MediaIn = TimeSpan.Zero,
                        MediaOut = null,
                        TimelineOffset = TimeSpan.Zero,
                        MediaDuration = TimeSpan.FromSeconds(25),
                        SyncOffset = TimeSpan.Zero,
                        FrameRate = null,
                        IsEnabled = false
                    }
                ]
            };

            var playlist = new PlaylistState();

            ProjectSerializer.ApplyToPlaylist(project, playlist);

            playlist.Tracks.Should().HaveCount(2);
            playlist.CurrentIndex.Should().Be(0);
            playlist.Current.Should().BeSameAs(playlist.Tracks[0]);

            var t0 = playlist.Tracks[0];
            t0.Id.Should().Be(project.Tracks[0].Id);
            t0.FilePath.Should().Be(Path.GetFullPath(tempFile1));
            t0.Name.Should().Be("Alpha Clip");
            t0.MediaIn.Should().Be(TimeSpan.FromSeconds(10));
            t0.MediaOut.Should().Be(TimeSpan.FromSeconds(50));
            t0.GetActualTimelineIn().Should().Be(TimeSpan.FromSeconds(5));
            t0.TimelineOffset.Should().Be(TimeSpan.FromSeconds(5));
            t0.MediaDuration.Should().Be(TimeSpan.FromSeconds(100));
            t0.SyncOffset.Should().Be(TimeSpan.FromMilliseconds(50));
            t0.FrameRate.Should().Be(30);
            t0.IsEnabled.Should().BeTrue();

            var t1 = playlist.Tracks[1];
            t1.Id.Should().Be(project.Tracks[1].Id);
            t1.FilePath.Should().Be(Path.GetFullPath(tempFile2));
            t1.Name.Should().Be("Beta Clip");
            t1.MediaIn.Should().Be(TimeSpan.Zero);
            t1.MediaOut.Should().BeNull();
            t1.GetActualTimelineIn().Should().Be(TimeSpan.Zero);
            t1.TimelineOffset.Should().Be(TimeSpan.Zero);
            t1.MediaDuration.Should().Be(TimeSpan.FromSeconds(25));
            t1.SyncOffset.Should().Be(TimeSpan.Zero);
            t1.FrameRate.Should().BeNull();
            t1.IsEnabled.Should().BeFalse();

            t1.GetActualTimelineOut().Should().Be(TimeSpan.Zero + TimeSpan.FromSeconds(25));
        }
        finally
        {
            File.Delete(tempFile1);
            File.Delete(tempFile2);
        }
    }

    [Fact]
    public async Task ApplyToPlaylist_EmptyProject_ClearsPlaylist()
    {
        var project = new ProjectData
        {
            Version = 1,
            SyncMode = SyncMode.Single,
            GapBehavior = GapBehavior.Black,
            Tracks = []
        };

        var playlist = new PlaylistState();
        playlist.AddFiles(["C:\\Videos\\existing.mp4"]);
        playlist.Tracks.Should().HaveCount(1);

        ProjectSerializer.ApplyToPlaylist(project, playlist);

        playlist.Tracks.Should().BeEmpty();
        playlist.CurrentIndex.Should().Be(-1);
        playlist.Current.Should().BeNull();
    }

    [Fact]
    public async Task SaveAndLoad_EmptyPlaylist_ProducesEmptyTracks()
    {
        var playlist = new PlaylistState();
        string filePath = GetTempPath("empty_playlist.tsp");

        await ProjectSerializer.SaveAsync(filePath, playlist, SyncMode.Single, GapBehavior.Freeze);

        var project = await ProjectSerializer.LoadAsync(filePath);

        project.Should().NotBeNull();
        project!.Tracks.Should().BeEmpty();
        project.SyncMode.Should().Be(SyncMode.Single);
        project.GapBehavior.Should().Be(GapBehavior.Freeze);
    }

    [Fact]
    public async Task SaveAndLoad_SingleTrack_RoundTripPreservesData()
    {
        var tempFile = GetTempPath("solo.mov");
        await File.WriteAllTextAsync(tempFile, "");

        try
        {
            var playlist = new PlaylistState();
            playlist.AddFiles([tempFile]);
            var track = playlist.Tracks[0] with
            {
                MediaIn = TimeSpan.FromSeconds(1.5),
                MediaDuration = TimeSpan.FromSeconds(120.75),
                FrameRate = 23.976,
                SyncOffset = TimeSpan.FromMilliseconds(33),
                Name = "Solo Track"
            };
            playlist.Tracks[0] = track;

            string filePath = GetTempPath("single.tsp");
            await ProjectSerializer.SaveAsync(filePath, playlist, SyncMode.Single, GapBehavior.Freeze);

            var project = await ProjectSerializer.LoadAsync(filePath);
            project.Should().NotBeNull();
            project!.Tracks.Should().ContainSingle();

            var loaded = project.Tracks[0];
            loaded.Id.Should().Be(track.Id);
            loaded.FilePath.Should().Be(Path.GetFullPath(tempFile));
            loaded.Name.Should().Be("Solo Track");
            loaded.MediaIn.Should().Be(TimeSpan.FromSeconds(1.5));
            loaded.MediaDuration.Should().Be(TimeSpan.FromSeconds(120.75));
            loaded.FrameRate.Should().Be(23.976);
            loaded.SyncOffset.Should().Be(TimeSpan.FromMilliseconds(33));
            loaded.IsEnabled.Should().BeTrue();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ApplyToPlaylist_RejectsPathTraversalPaths()
    {
        var tempFile = GetTempPath("valid.mp4");
        await File.WriteAllTextAsync(tempFile, "");

        try
        {
            var project = new ProjectData
            {
                Version = 1,
                SyncMode = SyncMode.Continue,
                GapBehavior = GapBehavior.Black,
                Tracks =
                [
                    new TrackData
                    {
                        Id = Guid.NewGuid(),
                        FilePath = "../../../Windows/System32/nonexistent_file_xyz.exe",
                        Name = "Traversal Track",
                        MediaIn = TimeSpan.Zero,
                        MediaOut = null,
                        TimelineOffset = TimeSpan.Zero,
                        MediaDuration = TimeSpan.FromSeconds(10),
                        SyncOffset = TimeSpan.Zero,
                        FrameRate = 30,
                        IsEnabled = true
                    }
                ]
            };

            var playlist = new PlaylistState();

            ProjectSerializer.ApplyToPlaylist(project, playlist);

            playlist.Tracks.Should().BeEmpty();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ApplyToPlaylist_SkipsTrackWithZeroFrameRate()
    {
        var tempFile = GetTempPath("valid.mp4");
        await File.WriteAllTextAsync(tempFile, "");

        try
        {
            var project = new ProjectData
            {
                Version = 1,
                SyncMode = SyncMode.Continue,
                GapBehavior = GapBehavior.Black,
                Tracks =
                [
                    new TrackData
                    {
                        Id = Guid.NewGuid(),
                        FilePath = tempFile,
                        Name = "Zero Fps Track",
                        MediaIn = TimeSpan.Zero,
                        MediaOut = null,
                        TimelineOffset = TimeSpan.Zero,
                        MediaDuration = TimeSpan.FromSeconds(10),
                        SyncOffset = TimeSpan.Zero,
                        FrameRate = 0,
                        IsEnabled = true
                    }
                ]
            };

            var playlist = new PlaylistState();

            ProjectSerializer.ApplyToPlaylist(project, playlist);

            playlist.Tracks.Should().BeEmpty();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ApplyToPlaylist_SkipsTrackWithNegativeFrameRate()
    {
        var tempFile = GetTempPath("valid.mp4");
        await File.WriteAllTextAsync(tempFile, "");

        try
        {
            var project = new ProjectData
            {
                Version = 1,
                SyncMode = SyncMode.Continue,
                GapBehavior = GapBehavior.Black,
                Tracks =
                [
                    new TrackData
                    {
                        Id = Guid.NewGuid(),
                        FilePath = tempFile,
                        Name = "Negative Fps Track",
                        MediaIn = TimeSpan.Zero,
                        MediaOut = null,
                        TimelineOffset = TimeSpan.Zero,
                        MediaDuration = TimeSpan.FromSeconds(10),
                        SyncOffset = TimeSpan.Zero,
                        FrameRate = -5.0,
                        IsEnabled = true
                    }
                ]
            };

            var playlist = new PlaylistState();

            ProjectSerializer.ApplyToPlaylist(project, playlist);

            playlist.Tracks.Should().BeEmpty();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ApplyToPlaylist_SkipsTrackWithNegativeTimelineOffset()
    {
        var tempFile = GetTempPath("valid.mp4");
        await File.WriteAllTextAsync(tempFile, "");

        try
        {
            var project = new ProjectData
            {
                Version = 1,
                SyncMode = SyncMode.Continue,
                GapBehavior = GapBehavior.Black,
                Tracks =
                [
                    new TrackData
                    {
                        Id = Guid.NewGuid(),
                        FilePath = tempFile,
                        Name = "Negative Offset Track",
                        MediaIn = TimeSpan.Zero,
                        MediaOut = null,
                        TimelineOffset = TimeSpan.FromSeconds(-1),
                        MediaDuration = TimeSpan.FromSeconds(10),
                        SyncOffset = TimeSpan.Zero,
                        FrameRate = 30,
                        IsEnabled = true
                    }
                ]
            };

            var playlist = new PlaylistState();

            ProjectSerializer.ApplyToPlaylist(project, playlist);

            playlist.Tracks.Should().BeEmpty();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ApplyToPlaylist_SkipsTrackWithEmptyFilePath()
    {
        var project = new ProjectData
        {
            Version = 1,
            SyncMode = SyncMode.Continue,
            GapBehavior = GapBehavior.Black,
            Tracks =
            [
                new TrackData
                {
                    Id = Guid.NewGuid(),
                    FilePath = "",
                    Name = "Empty Path Track",
                    MediaIn = TimeSpan.Zero,
                    MediaOut = null,
                    TimelineOffset = TimeSpan.Zero,
                    MediaDuration = TimeSpan.FromSeconds(10),
                    SyncOffset = TimeSpan.Zero,
                    FrameRate = 30,
                    IsEnabled = true
                }
            ]
        };

        var playlist = new PlaylistState();

        ProjectSerializer.ApplyToPlaylist(project, playlist);

        playlist.Tracks.Should().BeEmpty();
    }
}
