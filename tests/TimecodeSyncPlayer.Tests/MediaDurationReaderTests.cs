using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class MediaDurationReaderTests
{
    private readonly MediaDurationReader _reader = new();

    [Fact]
    public async Task ReadDurationAsync_RealVideoFile_ReturnsCorrectDuration()
    {
        Skip.IfNot(TestVideoFactory.FfmpegAvailable(), "ffmpeg is not available on PATH");

        string videoPath = TestVideoFactory.GetOrCreate();

        var duration = await _reader.ReadDurationAsync(videoPath);

        duration.Should().NotBeNull();
        duration.Value.TotalSeconds.Should().BeApproximately(20.0, 1.0);
    }

    [Fact]
    public async Task ReadDurationAsync_NonExistentFile_ReturnsNull()
    {
        string nonExistent = "/nonexistent/path/to/video_12345.mp4";

        var duration = await _reader.ReadDurationAsync(nonExistent);

        duration.Should().BeNull();
    }

    [Fact]
    public async Task ReadDurationAsync_EmptyFile_ReturnsNull()
    {
        string emptyFile = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "TimecodeSyncPlayer.Tests",
            "empty_video.mp4");

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(emptyFile)!);
        System.IO.File.WriteAllBytes(emptyFile, Array.Empty<byte>());

        try
        {
            var duration = await _reader.ReadDurationAsync(emptyFile);
            duration.Should().BeNull();
        }
        finally
        {
            try { System.IO.File.Delete(emptyFile); } catch { }
        }
    }
}
